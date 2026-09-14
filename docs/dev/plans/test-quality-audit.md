# Plan: Parsek unit-test quality audit

Baseline: `origin/main` at `4aedb0a1a018bd3420081778cffc1337bb6a58aa` (pinned; every finding cites this SHA).
Status: Phase 0 (instrumentation) in progress. Phase A is read-only on production and test code.

## Objective

Produce two evidence-backed artifacts before touching any test code:

1. **Test register** - every test file and every test method in `Source/Parsek.Tests`, with what it
   exercises, whether it can actually fail, and redundancy metadata.
2. **Issue register + coverage-opportunity backlog** - ranked, falsifiable findings
   (vacuous / redundant / misleading / brittle) and concrete new-test proposals for the covered SUTs.

Only after those are reviewed does Phase B (implementation in sibling worktrees, PR per workstream)
start. This audit is complementary to `test-coverage-audit-2026-07-29.md`: that audit asked "what is
missing at stack level"; this one asks "are the existing ~20k tests sound, and where is added
coverage worth it".

## Approved scope decisions (2026-09-14)

1. Scope: `Source/Parsek.Tests` xUnit unit tests only. `InGameTests` and the harness Python suites
   are cross-referenced only where they change a verdict on a unit test.
2. Inventory granularity: one row per test method (~20.2k rows), plus per-file summaries.
3. Depth: full tiered review of all 1,010 files; every file gets an agent pass.
4. Production bugs found during review are filed in a separate triage register (T7); they are never
   fixed inside this audit.
5. Phase B parallelism: 4 workstreams/worktrees at once.
6. This plan and the audit artifacts are committed to the repository.

## Recon facts (at baseline SHA)

- 1,010 test `.cs` files, ~23.8 MB; 19,436 `[Fact]` + 799 `[Theory]`; 604 production files in
  `Source/Parsek` excluding `InGameTests/`.
- Size mix: 24 files <2 KB, 246 at 2-8 KB, 520 at 8-32 KB, 206 at 32-128 KB, 14 >128 KB.
- Structure: flat test classes, no inheritance, no xUnit fixtures, one collection definition;
  ~72% of files are `[Collection("Sequential")]` (July audit measurement). Regex/script inventory is
  accurate and cheap at this shape.
- Shallow probes: 1 test-attribute file with zero `Assert.` (a dump utility), 0 `Assert.True(true)`,
  1 `Skip=`, 0 empty bodies. Value is in semantic issues, e.g. `BugFixTests.cs` contains
  near-duplicate tests and tests that simulate the production guard inline instead of exercising it.
- Smell indicators to triage: 100 files reading production source text, 324 `Guid.NewGuid`,
  7 `DateTime.Now/UtcNow`, 2 `Thread.Sleep`, 77 test-method names reused across files.
- Tooling: xUnit 2.4.2 + Moq + coverlet.msbuild; `scripts/test-coverage.ps1` yields per-class
  line/branch data. No mutation tooling exists (July audit D5), so mutation proof is bounded and
  scripted.
- Clusters (test-file counts): Route 89, wiring/contract `*Tests` 53, Ghost/Playback/Watch/Map/Render
  ~76, Recording/Chain/Merge/Session ~70, Rewind 22, Mission 19, Ledger/Crew/Kerbals ~35,
  Logistics 14, Spawn/Vessel/Debris ~33, Trajectory/Anchor ~30, UI/Settings ~30, long tail.

## Deliverables (Phase A)

All under `docs/dev/research/test-quality-audit-2026-09-14/` in worktree `../Parsek-test-audit`
(branch `test-quality-audit`):

- **D1 `test-inventory.csv`** - one row per test method:
  `file, class, method, kind(Fact/Theory), theory_cases, cluster, sut_guess, asserts(types used),
  log_only, source_text, verdict, register_id`. Machine-seeded in P0, agent-annotated in P1.
  A second file `file-summary.csv` holds per-file rows.
- **D2 `issue-register.md`** - ranked findings: `ID, severity (Critical/High/Medium/Low),
  category (T1-T7), file:line, what it asserts today, why it is weak/redundant (falsifiability
  argument), proposed action, effort (S/M/L), verification status (verified/refuted/pending),
  cluster`.
- **D3 `coverage-opportunities.md`** - per SUT: proposed test cases with name, arrange/act/assert
  sketch, and the regression each would guard; ranked by risk (data loss > career/ledger > recording
  integrity > playback > UI/cosmetic).
- **D4 `README.md`** - method, raw counts, top-20 lists, Phase-B proposal, links to the July audit
  and `design-testing-unified.md`.

Issue taxonomy:

- **T1 Vacuous/cannot-fail** - tautology, asserts on a copy, no meaningful assert, inline simulation
  of production logic, assertion weaker than the name.
- **T2 Redundant/duplicate** - same branch covered N times in-file or cross-file (normalized body
  hashing).
- **T3 Weak/misleading** - asserts implementation detail instead of contract, over-mocked, wrong
  tolerance, name/intent mismatch, culture/format coupling.
- **T4 Brittle/flaky** - shared static state without `Sequential`/reset, order dependence, wall
  clock, unbounded Guid assumptions, source-text needle where a behavioral test is feasible.
- **T5 Coverage opportunity** - uncovered guard/branch, missing negative cases, missing mirror
  direction (encode/decode, carrier/non-carrier), missing serialization keys.
- **T6 Organization** - mega-files, bug-number graveyard files, fixture duplication, generator
  ceilings blocking testability.
- **T7 Production bug found** - separate triage register; never fixed inside this audit.

## Phases

### Phase 0 - baseline and instrumentation (mechanical)

- P0.1 Pin baseline SHA; create the audit worktree. Done.
- P0.2 One full `dotnet test` run with TRX logger: baseline green/red, wall time, slowest classes.
- P0.3 Coverage run (`test-coverage.ps1 -Format cobertura`); parse to per-class line/branch, join
  into D1.
- P0.4 Inventory script (Python, stdlib) generating D1 rows from attributes/classes/collections/
  `IDisposable`/sink usage.
- P0.5 Smell sweep scripts emitting candidate findings with confidence (`mechanical` vs
  `needs-review`): tautologies, no-assert bodies, log-only files, source-text files classified as
  wiring-gate vs behavior-test, normalized-body duplicate pairs, name duplicates, shared-state
  leaks, time/random/culture patterns, mega-file lists.
- P0.6 Cluster map: every file assigned to one of ~14 review clusters, tiered
  A (>32 KB), B (8-32 KB), C (2-8 KB), D (<2 KB).

### Phase 1 - full-file review (every file gets an agent pass; depth tiered)

Per-agent protocol (inputs: its test files + primary SUT sources + checklist + relevant July audit
extracts):

- For each test: state intent, state what it asserts, run the falsifiability test ("what production
  change makes this red; if none, why"), verdict, redundancies, coverage suggestions.
- Agents never report style opinions as issues; findings without a falsifiability argument are
  rejected.
- Batch sizes: Tier A 3-6 files, Tier B 8-12, Tier C 15-25, Tier D mechanically classified plus spot
  check. Roughly 100-140 agent passes, 6-8 parallel per round.
- Output: per-batch fragment files under `fragments/` (markdown findings + CSV rows); agents return
  only a short summary to the supervisor.

### Phase 2 - adversarial verification of findings

- Every Critical/High candidate: a second agent tries to refute it, reading the SUT; where cheap, a
  bounded mutation on a scratch worktree (stub the guarded production line, confirm the test stays
  green if the finding holds). Mutations never commit.
- Supervisor spot-checks 10% of Medium findings and 100% of Critical.
- Refuted findings are recorded with the refutation.

### Phase 3 - coverage-opportunity analysis (per high-value SUT)

- For decision-dense SUTs (ledger modules, recorder/playback, rewind/supersede, optimizer,
  serialization codecs, spawn paths): uncovered branches/guards, missing negative cases,
  mirror-direction checks, serializer key round-trips. Concrete proposals only.
- Re-check the July audit's still-open Tier E leftovers so D3 does not duplicate them.

### Phase 4 - synthesis and operator gate

- Dedup, rank (player consequence x cheapness), publish D1-D4, propose Phase-B workstreams.
  Implementation starts only on explicit go-ahead.

### Phase B - implementation (outline)

- One sibling worktree per workstream (`../Parsek-testfix-<topic>`; test-only work still needs a
  sibling worktree because the test project builds against KSP refs).
- Each item's PR cites register IDs; every added or strengthened test needs a mutation proof
  (revert the guarded line, show red, restore) recorded in the PR body.
- Wave order: (1) T1/T2 fixes, (2) T4 flakiness, (3) T5 coverage additions, (4) T7 production bugs
  as separate PRs. Full suite green before each PR.
- Register docs updated per commit; `CHANGELOG` only for T7 production fixes.

## Controls and risks

- No production or test edits during Phase A.
- Budget is dominated by Phase 1 reading; tiers and batching are the governor. Progress is reported
  per round; the audit can stop early if cost/value turns.
- Coverage is a diagnostic, never a target. The output is a ranked backlog, not a mandate.
- Repeat full-suite runs only at phase gates; use `--filter` per class during review.
