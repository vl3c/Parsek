# Development Workflow

Generic workflow for implementing features and larger tasks in the Parsek project. Balances thorough design with efficient execution by using Claude agents at each stage.

---

## Overview

```
1. Vision  →  2. Scenarios  →  3. Design Doc  →  4. Plan/Build/Review Cycle  →  5. Commit
   (idea)      (gameplay sim)    (formalize)      (clean-context agents)         (merge)

   ◄── gameplay design (abstract) ──►  ◄── code exploration ──►  ◄── implementation ──►
       no code, no codebase                enters at planning        agents in worktrees
```

The workflow has two modes depending on task size:

- **Small tasks** (bug fix, single-file change, known pattern): skip to step 4, work directly.
- **Large tasks** (new feature, multi-file refactor, unknown territory): follow all steps. The design document is the artifact that prevents wasted work.

**Key ordering principle:** Steps 1-3 are about **gameplay design** - what the player experiences, how the feature fits the game. No code exploration, no codebase diving. Code exploration happens in step 4 when the Plan agent reads the codebase to turn the approved design into implementation tasks. This separation prevents implementation concerns from contaminating gameplay decisions.

### The Clean-Context Subagent Principle

Each stage of implementation uses a **fresh subagent with clean context**. This is deliberate:

- A **Plan agent** reads the design doc and codebase cold, producing a plan unbiased by prior conversation
- A **Review agent** reads the plan (or code) cold, catching issues that the author's context would hide
- An **Implementation agent** reads the plan and design doc cold, following instructions without accumulated assumptions
- A **Review agent** reads the implementation cold, comparing it against the design doc and existing patterns

Each agent gets: the design doc path, relevant file paths, and a specific task description. Nothing else. Clean context prevents drift, groupthink, and the "I already know this code" blindness that causes bugs.

The **orchestrator** (main Claude session or human) is the only entity that maintains continuity across agents. It dispatches work, reviews agent output, decides whether to iterate or proceed, and handles the merge.

---

## Step 1: Feature Vision (Human-Driven)

State the high-level idea. What is this feature? How does it help the player? How does it fit into the game's flow?

**No code, no codebase, no technical concerns.** This is pure gameplay thinking.

**What to produce:**
- A one-paragraph description of what the feature is and why it matters
- How it fits into the existing gameplay loop (when does the player encounter it? what problem does it solve for them?)
- What the player's experience should feel like (seamless? explicit choice? automatic?)

**Example:** "Parsek currently records one vessel at a time. Real KSP missions involve multiple vessels created through undocking, EVA, and staging. When the player reverts, all vessels that existed at the end should be accounted for." - This is a gameplay statement, not a technical one. It says what's wrong from the player's perspective.

**How to approach it:**
- Think as a player, not a developer
- Ask: "If this feature existed, how would my next play session be different?"
- Ask: "What's the simplest version of this that would still feel good?"
- Don't worry about feasibility yet - that comes later

This step is interactive - the human describes what they want, Claude asks as many clarifying questions as needed to build a thorough world model of the vision. No agents, no automation, no code.

**Claude's role here is to interrogate the idea, not accept it passively.** Ask about:
- What inspired this feature? What gameplay moment made you want it?
- Who is this for - the casual player, the optimizer, the role-player?
- How prominent should this be? Always visible, or discoverable?
- What's the emotional payoff? What should the player feel when using it?
- What existing feature is this most similar to in spirit?
- What would make you disappointed in how this turned out?

The goal is for Claude to have a mental model of the vision that's detailed enough to make design decisions autonomously in step 3 - without having to ask "but what did you mean by X?" during the design doc. Front-load the questions here.

---

## Step 2: Gameplay Scenarios (Human + Claude)

Walk through concrete gameplay sessions mentally. Simulate what the player does, step by step, to form a mental image of how the feature works in practice.

**Still no code.** This is gameplay simulation at the ideas level.

**What to produce:**
- 3-5 concrete play sessions that exercise the feature, written as "the player does X, then Y happens, then the player sees Z"
- The happy path (everything works as intended)
- The messy paths (what happens when things go sideways - destruction, revert, unexpected player choices)
- Interactions with existing features (crew, resources, ghosts, recordings, time warp)

**How to think about scenarios:**
- Pick a real mission (Mun landing, space station assembly, rescue mission) and play through it mentally with the feature enabled
- At each decision point, ask: "What does the player expect to happen here? What would surprise them?"
- Consider the player who doesn't read docs - would the feature behave intuitively?
- Consider the player who tries to break things - what happens if they spam EVA, undock rapidly, quickload mid-action?

**Example:** A multi-vessel recording design walked through a full Mun expedition - composite vessel undocking an orbiter, then undocking a lander, then EVA, then boarding - and at each branch point defined exactly what the player sees and what happens to each vessel. It also covered the player switching between vessels, going to the Space Center, and coming back. These scenarios existed before any data model was designed.

**Claude should actively probe for gaps in the scenarios.** Don't wait for the human to think of edge cases - propose them:
- "What happens if the player does X right after Y?"
- "You described the happy path - what if the vessel is destroyed mid-way?"
- "Does this interact with time warp? What should happen if the player warps past the moment where this feature matters?"
- "What if there are 10 of these? 50? Does the feature still feel good at scale?"
- "What does the player see in the UI? Is there a new window, a button, a notification?"
- "What if the player ignores this entirely - does the game still work normally?"

**The output of this step is a shared mental model** between human and Claude of how the feature behaves in gameplay. Both parties should be able to answer "what happens if the player does X?" without hesitating. If there's ambiguity, keep discussing until it's resolved. Claude should not move to step 3 until confident that no major gameplay question is left unanswered.

This step can also surface features that sounded good in step 1 but don't actually work in practice. Better to discover that now than after writing code.

---

## Step 3: Design Document (Human + Claude)

Formalize the mental model from step 2 into a design document. This is where gameplay decisions get translated into concrete structures and behaviors - but the gameplay scenarios drive the design, not the other way around.

**The design document is the central artifact.** It prevents the most expensive kind of waste: building the wrong thing, or building something that doesn't handle edge cases.

**Structure (extracted from project design docs):**

```markdown
# Design: [Feature Name]

## Problem
One paragraph. What's broken or missing. Why it matters to the player.

## Terminology
Define any new concepts. Separate your model from KSP's model.
Keep this short - only terms that would confuse someone reading the doc.

## Mental Model
How the feature works conceptually. Diagrams if helpful.
ASCII art trees/flowcharts are fine. Keep it visual.
This section captures the shared understanding from step 2.

## Data Model
New structs, fields, enums. Show the shape of the data.
Include serialization format (ConfigNode keys) if it persists to save files.

## Behavior
What happens at each trigger point. Use concrete scenarios:
- "When the player does X, Y happens"
- "When event Z fires, the system does W"
These come directly from the gameplay scenarios in step 2,
now with enough precision to implement.

## Edge Cases
Exhaustive. Every scenario that could go wrong or behave unexpectedly.
Each edge case gets:
- The scenario (what triggers it)
- The expected behavior (what should happen)
- Whether it's handled in v1 or deferred

## What Doesn't Change
Explicitly list systems that are NOT affected. Prevents scope creep
and gives reviewers confidence that existing behavior is preserved.

## Backward Compatibility
How existing saves/recordings/data migrate or coexist.

## Diagnostic Logging
Every decision point, state transition, and edge case must produce a log line.
For each section in Behavior and Edge Cases above, list the log lines it emits:
- What subsystem tag to use (e.g., "Recorder", "Spawner", "GhostVisual")
- Decision points: "if X, log why we chose path A vs B"
- State transitions: "when state changes from X to Y, log old→new with context"
- Edge case handling: "when edge case Z triggers, log the inputs that caused it"
- Error/fallback paths: "when X fails, log what failed and what fallback was used"
Goal: a developer reading the log should be able to reconstruct what the system
did and why, without looking at source code. Every branch that silently picks
one path over another is a debugging blind spot.

## Test Plan
List the tests this feature requires. Every test must have a concrete
"what makes it fail" justification - if you can't state what bug the test
would catch, the test is vacuous and shouldn't exist.

Categories to cover:
- **Unit tests**: pure logic, data transformations, serialization round-trips.
  Each test: input → expected output → what regression it guards against.
- **Integration tests**: multi-component interactions testable without the
  full game runtime (e.g., synthetic recordings through store → scenario →
  playback math). Use test generators (RecordingBuilder, VesselSnapshotBuilder,
  ScenarioWriter) to build realistic fixtures.
- **Log assertion tests**: capture log output via the test sink and assert
  that specific decision points produce expected log lines. This verifies
  both the behavior AND the diagnostic coverage - if a log line disappears,
  the test fails, preventing silent loss of observability.
- **Edge case tests**: one test per edge case listed above. The test
  reproduces the edge case scenario and verifies the documented behavior.
```

**Design doc principles (learned from this project):**

1. **Exhaust the edge cases.** A good design doc has 15+ edge cases, each with a concrete scenario and a decision. This is where most bugs are prevented - not in code review, not in testing. Write them all down.

2. **State what you're NOT doing.** "What doesn't change" and "Out of scope" sections prevent scope creep and keep the implementation focused.

3. **Separate v1 from future.** Mark edge cases as "acceptable v1 limitation" when perfect handling is complex and the scenario is rare. Ship something that works for 95% of cases.

4. **Show the data model.** Concrete struct/class layouts with field names and types. Not UML - just indented text showing the shape. This catches design issues that prose descriptions miss.

5. **Include serialization.** If it persists (save files, sidecar files), show the ConfigNode/file format. Serialization bugs are the hardest to fix after release.

6. **Design the logging, not just the code.** The Diagnostic Logging section isn't an afterthought - it's part of the design. Every behavior and edge case section entry should have a corresponding log line. If you can't describe what to log at a decision point, the decision itself isn't well-enough understood. A feature without diagnostic logging is a feature you can't debug in production.

7. **Design the tests, not just the feature.** The Test Plan section forces you to think about verifiability during design, not after implementation. Every test must justify its existence: "this test catches the bug where X happens because Y." If you can't state what makes it fail, it's testing the compiler, not the feature. Include log assertion tests - they pull double duty by verifying behavior AND ensuring diagnostic coverage survives refactoring.

**How to write it:**
- Human and Claude already share the mental model from steps 1-2 - the heavy questioning is done
- Claude should be able to draft most of the design doc autonomously from the shared understanding, checking back with the human only on genuinely ambiguous decisions
- Human reviews, corrects, and refines - but the doc should be 80% right on the first pass because the vision was thoroughly interrogated
- Iterate until edge cases are exhaustive and the data model is concrete
- The doc lives in `docs/` and is referenced from the roadmap

**Note:** The design doc is written without deep codebase exploration. It describes *what* the system should do, not *how* the existing code needs to change. Code exploration happens in step 4 when the Plan agent maps the design onto the codebase.

---

## Step 4: Plan → Build → Review Cycle (Clean-Context Agents)

Implementation uses a repeating cycle of clean-context subagents. Each agent starts fresh - no accumulated context from prior agents. The orchestrator (main Claude session or human) drives the cycle.

### 4a. Explore + Plan (Agents - clean context)

This is where code exploration enters the workflow. The design doc says *what* the system should do; now we figure out *how* the existing codebase needs to change to make it happen.

**First: Explore the codebase** using Explore agents to map the territory:

```
Agent(subagent_type=Explore):
  "Read docs/design-[feature].md. Then investigate the current codebase:
   - Which source files will be affected?
   - What KSP APIs/events are relevant? (check docs/mods-references/ and MEMORY.md for known gotchas)
   - How does existing code handle similar problems? (patterns to reuse)
   - What data structures exist and how do they serialize?
   - What tests cover the affected area?
   Return file paths, relevant code patterns, and gotchas."
```

Multiple Explore agents can run in parallel for independent questions (e.g., one for KSP API surface, one for existing serialization patterns).

**Then: Plan the implementation** using a Plan agent that reads both the design doc and the exploration results:

```
Agent(subagent_type=Plan):
  "Read docs/design-[feature].md and the current codebase. Break the
   implementation into ordered phases. Each phase should be independently
   testable. Identify dependencies between phases. List the specific files
   to create/modify in each phase. For each task, specify: what to change,
   which files, what the test/verification looks like."
```

**Plan output location:** The plan is ephemeral - it lives in the Plan agent's response, not in a file. The orchestrator reads it, reviews it, and translates the approved tasks into TaskCreate calls. The plan itself is consumed and doesn't need to persist; the design doc (in `docs/`) and the task list are the durable artifacts.

**Phase structure:**
- Each phase produces a working, testable increment
- Phases are ordered by dependency (data model first, then behavior, then UI, then edge cases)
- Each phase has a clear "done" condition (tests pass, specific behavior works)

**Task granularity:**
- A task should be completable in one agent session (roughly: one logical change across 1-3 files)
- If a task touches more than 3 files or requires understanding multiple subsystems, split it further
- Each task description includes: what to change, which files, what the test/verification looks like

### 4b. Review the Plan (Orchestrator)

The orchestrator reads the plan and checks:
- Does the phase ordering make sense? Are dependencies correct?
- Does each task have a clear scope and done condition?
- Are there tasks missing for edge cases listed in the design doc?
- Is parallelization possible between any tasks?

If the plan needs adjustment, the orchestrator either edits it directly or re-runs the Plan agent with feedback. **Do not start implementation with an unclear plan.**

Once approved, create tasks:
```
TaskCreate: "Add BranchPoint struct and serialization"
  description: "Create BranchPoint struct in new file BranchPoint.cs.
    Fields: id, ut, type (enum), parentRecordingIds, childRecordingIds.
    Serialization: BRANCH_POINT ConfigNode with repeated parentId/childId keys.
    Test: round-trip serialization test in BranchPointTests.cs."
```

Set up dependencies with `TaskUpdate(addBlockedBy: [...])` so tasks execute in the right order.

### 4c. Implement (Implementation Agent - clean context)

Each task is handled by a fresh general-purpose agent. The orchestrator decides the isolation level based on task scope:

**Isolation modes:**
- **No isolation (default for small/medium tasks):** The agent works directly on the current branch. The orchestrator stays in the same worktree and dispatches agents sequentially. Best for tightly coupled sequential tasks within a single feature (e.g., data model → builder → tests), where each task builds on the previous commit. Avoids the overhead of creating/merging worktree branches for every small unit of work.
- **Isolated worktree (for large or parallel tasks):** The `isolation=worktree` parameter creates an automatic worktree under `.claude/worktrees/`. Best for independent tasks that can run in parallel, tasks that might need to be discarded, or large changes where isolation protects the main branch. Use `run_in_background: true` for parallel agents.

**Choosing isolation level:**

| Situation | Isolation |
|-----------|-----------|
| Sequential tasks on a feature branch (3-5 small files) | No isolation — work directly on branch |
| Independent tasks that can run in parallel | Isolated worktrees — merge results after |
| Large task touching many files (risky, might need rollback) | Isolated worktree — discard if it goes wrong |
| First task setting a pattern others will follow | No isolation — orchestrator reviews before continuing |

The agent receives: the task description, the design doc path, and which existing files to follow as patterns.

```
Agent(subagent_type=general-purpose):
  "Implement task: [task description from TaskCreate].
   Design doc: docs/design-[feature].md
   Follow patterns in [existing similar file].
   Run dotnet build to verify compilation.
   Run dotnet test to verify all tests pass.
   Commit with a descriptive message."
```

**Rules for implementation agents:**
- Read the design doc before writing code
- Follow existing code patterns (naming, error handling, serialization style)
- **Add diagnostic logging to every decision point and state transition.** Follow the Diagnostic Logging section of the design doc. Use the project's structured logging API with appropriate subsystem tags and levels (`Info` for state transitions visible in normal operation, `Verbose` for per-frame/high-frequency diagnostics, `Warn`/`Error` for unexpected conditions). Every `if/else` that picks a non-obvious path, every fallback, every skip gets a log line explaining why.
- **Write tests alongside implementation (not after).** Follow the Test Plan section of the design doc. Every test must have a "what makes it fail" justification. Include log assertion tests that capture output via the test sink and verify that decision points produce expected diagnostic lines. Tests without a concrete regression they guard against are noise - don't write them.
- Run `dotnet build` and `dotnet test` before committing
- One logical change per commit

### 4d. Review the Implementation (Review Agent - clean context)

After each task (or after each phase, for smaller tasks), a fresh review agent examines the work. The reviewer has never seen the implementation - it reads the diff, the design doc, and the existing codebase cold.

```
Agent(subagent_type=general-purpose):
  "Review the changes on branch [branch-name] against docs/design-[feature].md.
   First, run 'dotnet build' and 'dotnet test' independently to verify.
   Then check:
   1. Does the implementation match the design doc's data model and behavior?
   2. Are edge cases from the design doc handled?
   3. Does the code follow existing patterns in [similar file]?
   4. Is serialization correct (ConfigNode keys, round-trip)?
   5. Are tests present and do they cover the task's scope?
   6. Any bugs, missing null checks, or serialization issues?
   Report issues found. Do NOT fix them - just list them."
```

**Review checklist (from CLAUDE.md Post-Change Checklist):**
1. Save serialization - does `ParsekScenario.cs` OnSave/OnLoad handle new data?
2. Synthetic recording injector - can `RecordingBuilder`/`VesselSnapshotBuilder`/`ScenarioWriter` produce test data for the new feature?
3. New synthetic recording - does a new test recording exercise the feature end-to-end?
4. All existing tests pass
5. **Diagnostic logging coverage** - does every decision point, state transition, and edge case in the new code produce a log line? Compare against the design doc's Diagnostic Logging section. Silent branches are review failures. Check that log lines include enough context to reconstruct what happened (relevant IDs, old→new values, the "why" not just the "what").
6. **Test quality** - does every new test have a concrete "what makes it fail" justification? Are there log assertion tests that verify diagnostic output via the test sink? Are edge cases from the design doc covered by dedicated tests? Reject vacuous tests that can't fail for any realistic bug.

### 4e. Fix (Implementation Agent - clean context, if issues found)

If the review found issues, a new implementation agent gets the review feedback and fixes them. **Not the same agent** - a fresh one, so it reads the code without the original author's assumptions.

```
Agent(subagent_type=general-purpose, isolation=worktree):
  "Fix the following issues found in review of [branch-name]:
   [list of issues from review agent]
   Design doc: docs/design-[feature].md
   Run dotnet build and dotnet test after fixes.
   Commit fixes separately from the original implementation."
```

Then review again (4d) if the fixes were non-trivial.

### 4f. Repeat for Next Phase

Once a phase passes review, the orchestrator:
- Marks tasks as completed
- Merges the worktree branch if desired (or accumulates for end)
- Moves to the next phase's tasks
- Loops back to 4c

### The Full Cycle Visualized

```
                    ┌─────────────────────────────────┐
                    │         ORCHESTRATOR             │
                    │  (main session / human)          │
                    │  - maintains continuity          │
                    │  - dispatches agents             │
                    │  - decides proceed vs. iterate   │
                    └──────────┬──────────────────┬────┘
                               │                  │
            ┌──────────────────┘                  └──────────────────┐
            ▼                                                        ▼
   ┌─────────────────┐    approve/    ┌──────────────────┐    issues?   ┌─────────────────┐
   │   Plan Agent    │───iterate────▶│  Implement Agent  │───────────▶│  Review Agent    │
   │  (clean context)│               │  (clean context,  │            │  (clean context) │
   │                 │               │   worktree)       │            │                  │
   │  reads: design  │               │  reads: design,   │            │  reads: diff,    │
   │  doc, codebase  │               │  plan, patterns   │            │  design doc,     │
   │                 │               │                    │            │  codebase        │
   │  produces: plan │               │  produces: code,   │            │  produces: issue │
   │  with phases    │               │  tests, commit    │            │  list or "clean" │
   └─────────────────┘               └──────────────────┘            └────────┬──────────┘
                                              ▲                               │
                                              │         issues found          │
                                              │                               │
                                     ┌────────┴──────────┐                    │
                                     │   Fix Agent       │◀───────────────────┘
                                     │  (clean context,  │
                                     │   worktree)       │
                                     │                   │
                                     │  reads: review    │
                                     │  feedback, code   │
                                     └───────────────────┘
```

### When to Use Full Cycle vs. Shortcut

| Situation | Approach |
|-----------|----------|
| First task in a new area (sets the pattern) | Full cycle: plan → implement → review → fix |
| Tasks that change serialization format | Full cycle with extra review attention on round-trip |
| Well-defined task following existing pattern | Shortcut: implement → final review (skip separate plan) |
| Low-risk small single-file fix, docs-only change, test-only change, or obvious bug fix with focused validation | Shortcut: implement directly, self-review, report validation |
| Design doc says "needs investigation" | Research agent first, then full cycle |

### When to Intervene vs. Let Agents Work

- **Let agents work autonomously:** well-defined tasks with clear scope, existing patterns to follow, straightforward test criteria
- **Orchestrator should intervene:** first task in a new area (sets the pattern), tasks that change serialization format, tasks that modify public API surface, review agent found architectural issues (not just bugs)
- **Human should intervene:** design doc ambiguity discovered during implementation, scope creep detected, fundamental approach questioned by review agent

### When Things Go Wrong

**Plan agent produces a bad plan (twice):** Stop. The design doc is probably ambiguous or missing information. Go back to step 2-3 and refine the design doc with the human before re-planning. Don't keep re-running the Plan agent hoping for a better result.

**Implementation agent can't complete a task:** Check if the task description is too vague, the scope is too large, or there's a missing dependency. Split the task, add more context to the description, or have the orchestrator resolve the blocker before re-dispatching.

**Review finds architectural issues (not just bugs):** This means the plan or design doc has a gap. Don't patch around it - escalate to the orchestrator/human. Options:
- If the issue is contained: update the design doc, adjust remaining tasks, continue
- If the issue invalidates the approach: stop implementation, revise the design doc (back to step 3), re-plan

**Build or tests fail after merge:** The worktree branches diverged. Don't force it. Read the conflicts, understand the root cause, and resolve manually or dispatch a clean-context agent to handle the merge.

**An agent loops or gets stuck:** Kill it. Don't let agents retry the same failing approach. Diagnose what went wrong, adjust the task description or approach, and dispatch a fresh agent.

The general principle: **escalate up, don't brute-force forward.** If step 4 reveals problems, the fix is usually in step 3 (design) or step 2 (scenarios), not in more implementation attempts.

### Manual Testing (Larger Features)

After all phases pass automated review, do a manual in-game verification:
- Inject synthetic recordings: `dotnet test --filter InjectAllRecordings`
- Launch KSP, load test career, verify in-game behavior
- Check KSP.log: `grep "[Parsek]" "Kerbal Space Program/KSP.log"`
- Run log validator: `pwsh -File scripts/validate-ksp-log.ps1`
- Verify the deployed `GameData/Parsek/Plugins/Parsek.dll` against your
  worktree build using the `.claude/CLAUDE.md` DLL-check recipe before trusting
  any in-game result. For release / RC evidence, compare against the Release
  output (`Source/Parsek/bin/Release/Parsek.dll`), not the local Debug build.
- For release or RC closeout, reset the in-game test results before each
  evidence run, then capture the required bundles from
  `docs/dev/manual-testing/test-general.md` with
  `python scripts/collect-logs.py <label>`. Keep the emitted `KSP.log`,
  `Player.log`, `parsek-test-results.txt`, and `log-validation.txt`, then run
  `python scripts/validate-release-bundle.py <bundle-dir>` and keep the emitted
  `release-bundle-validation.txt`; the validators are per-bundle gates, not
  end-of-run spot checks.

---

## Step 5: Commit and Update (Human + Claude)

**Merge worktree branches:**

Agent worktrees (created via `isolation=worktree`) live under `.claude/worktrees/` and return the branch name in their result. Merge from the main repo:
```bash
git merge <branch-name-from-agent-result>
```

If using manual worktrees (sibling folders per `.claude/CLAUDE.md`):
```bash
cd Parsek && git merge <branch-name>
git worktree remove ../Parsek-<branch-name>
```

**Update project docs:**
- Move completed items in `docs/roadmap.md` to the "Completed" section
- Update `MEMORY.md` with new patterns, gotchas, or architectural decisions discovered during implementation
- Update the root `CLAUDE.md` if new files, test workflows, or debug procedures were added (this is the canonical copy - the inner `.claude/CLAUDE.md` mirrors worktree-specific instructions only)

**Clean up:**
- Delete completed tasks
- Agent worktrees are auto-cleaned if no changes were made; worktrees with changes persist until merged

---

## Decision Framework: When to Design vs. Just Build

| Signal | Action |
|--------|--------|
| Touches 1-2 files, pattern exists | Skip to 4c: implement → review |
| Bug fix with clear cause | Skip to 4c: implement directly, review if multi-file |
| Multi-file change, known pattern | Skip to 4a: explore + plan, then implement → review |
| Pure internal refactor (no gameplay change) | Skip to 4a: explore + plan + build (no design doc) |
| New data that persists to save files | Full workflow from step 1 |
| Multiple valid approaches | Full workflow - gameplay scenarios (step 2) will clarify |
| Edge cases are non-obvious | Full workflow - scenario simulation (step 2) will surface them |
| Affects how the player experiences the game | Full workflow - starts with vision (step 1) |
| Roadmap item with its own section | Full workflow from step 1 |
| User says "just do it" for something simple | Skip to 4c, but mention risks if any |

---

## Anti-Patterns to Avoid

**Accepting the vision without questioning it.** Claude should ask many clarifying questions during steps 1-2, not politely agree and move on. Every ambiguity left unresolved here becomes a wrong assumption in the design doc and a bug in the code. It's cheaper to ask "what did you mean by X?" in step 1 than to discover the misunderstanding in step 4.

**Diving into code before understanding gameplay.** The first question is always "what does the player experience?" not "what does the code look like?" Code exploration that happens before gameplay scenarios are clear will bias the design toward what's easy to implement rather than what's right for the player.

**Starting to code before understanding edge cases.** A thorough design doc should have 15+ edge cases for any non-trivial feature. Each one discovered during implementation would cost 5-10x more to fix than catching it in the doc. Gameplay scenario simulation (step 2) is where most edge cases are found.

**Over-designing simple changes.** A bug fix or a new setting doesn't need a design doc. If the change is obvious and self-contained, skip to step 4c (implement directly).

**Agents working without context.** Always point implementation agents at the design doc and existing patterns. An agent that doesn't read the codebase first will reinvent things that already exist.

**Reusing agent context across stages.** The same agent that wrote code should NOT review it - it has the author's blind spots baked in. Clean-context review agents catch issues that the author would rationalize away. This is the single biggest quality lever in the workflow.

**Skipping review for risky changes.** Small single-file fixes, docs-only changes, test-only changes, and obvious bug fixes with focused validation can use self-review. Behavioral, multi-file, serialization, runtime-only, or release-critical changes still need a clean-context final review.

**Skipping the post-change checklist.** Serialization bugs and test gaps are the most common regressions. The checklist exists because these were missed before.

**Parallelizing dependent work.** Two agents editing the same file or depending on each other's output will produce merge conflicts and inconsistent code. Use `blockedBy` relationships.

**Not updating MEMORY.md.** Hard-won gotchas (like KSP converting underscores to dots in part names, or `GameScenes.TRACKSTATION` not `TRACKINGSTATION`) save hours on future tasks. If you learn something surprising, write it down.

**Silent code paths.** An `if/else` with no logging on one branch is a debugging blind spot. When something goes wrong in-game, the only tool is KSP.log - if the code didn't log why it chose a particular path, you're left guessing. Every non-obvious branch should explain itself in the log. This is especially true for fallback paths and edge case handlers that rarely execute - those are exactly the paths that are hardest to debug without logging.

**Vacuous tests.** A test that asserts `result != null` or `list.Count > 0` without checking the actual value guards against almost nothing. Every test should have a stated regression it catches - "this test fails if X is broken because Y." Tests without this justification are false confidence. Similarly, testing only the happy path while the design doc lists 15 edge cases means the most important scenarios are unverified. Log assertion tests (capturing output via the test sink and asserting specific log lines appear) serve double duty: they verify behavior AND ensure diagnostic coverage survives refactoring.
