# Plan: Parsek unit-test quality audit

Baseline: `origin/main` at `4aedb0a1a018bd3420081778cffc1337bb6a58aa` (pinned; every finding cites this SHA).
Status: Phase 1 CLOSED 2026-09-15 (427 linted fragments, D1 complete at 20,225 rows, 768 findings, 252 coverage candidates; `docs/dev/research/test-quality-audit-2026-09-14/phase1-summary.md`). Phase 2 CLOSED 2026-09-15 (7/7 High verified by mutation, 27-sample Medium: 26 verified / 1 downgraded / 0 refuted; `phase2-summary.md`, `mutations/`). Phase 3 CLOSED 2026-09-15 (245 proposals kept of 252, each with a named mutant; `coverage-opportunities.md` / `.csv`). Phase 4 synthesis DONE 2026-09-15: `docs/dev/test-quality-audit-2026-09-14.md` (D2/D3/D4, T7 register, Phase B assignment, July crosswalk). AWAITING operator go-ahead for Phase B.
Phase A is read-only on production and test code; mutations run only in a detached scratch worktree
(never in this worktree, never committed).
Phase 0 results so far: full suite PASS, Failed 0 / Passed 23,420 / Skipped 1, wall 1m09s (TRX
`work/baseline.trx`); coverlet: line 45.75%, branch 47.4%, method 60.68% (`work/coverage.cobertura.xml`).

## Objective

Produce two evidence-backed artifacts before touching any test code:

1. **Test register** - every test file and every test method in `Source/Parsek.Tests`, with what it
   exercises, whether it can actually fail, and redundancy metadata.
2. **Issue register + coverage-opportunity backlog** - ranked, falsifiable findings
   (vacuous / redundant / misleading / brittle) and concrete new-test proposals for the covered SUTs.

Only after those are reviewed does Phase B (implementation in sibling worktrees, PR per workstream)
start. This audit is complementary to `test-coverage-audit-2026-07-29.md`: that audit asked "what is
missing at stack level"; this one asks "are the existing ~23.4k executed cases sound, and where is
added coverage worth it".

## Approved scope decisions (2026-09-14)

1. Scope: `Source/Parsek.Tests` xUnit unit tests only. `InGameTests` and the harness Python suites
   are cross-referenced only where they change a verdict on a unit test.
2. Inventory granularity: one row per test method (~20,235 methods; ~23.4k executed cases after
   theory expansion), plus per-file summaries.
3. Depth: full tiered review of all 1,010 files; every file gets an agent pass.
4. Production bugs found during review are filed in a separate triage register (T7); they are never
   fixed inside this audit.
5. Phase B parallelism: 4 workstreams/worktrees at once.
6. This plan and the audit artifacts are committed to the repository.

## Deliverables

Durable dogfood document (created in Phase 4, updated per phase):
- `docs/dev/test-quality-audit-2026-09-14.md` - the human-readable audit: method, counts,
  **D2 issue register**, **D3 coverage-opportunity backlog**, D4 summary. Sits beside the two prior
  dated audits at the `docs/dev/` root so the doc map can point at it.

Raw and derived evidence under `docs/dev/research/test-quality-audit-2026-09-14/`:
- `tools/` - committed stdlib Python: `inventory_scan.py`, `smell_sweep.py`, `parse_results.py`,
  `lint_fragments.py`, `build_batches.py`, `redundancy.py`, `merge_fragments.py`.
- `rubric.md` - committed, frozen review rubric (enums and definitions, see below). This exact text
  is embedded in every Phase 1 agent prompt.
- `test-inventory.csv` - **D1**, one row per test method, written only by `merge_fragments.py`
  (agents never hand-edit CSV; they emit JSONL). Columns pinned in the rubric.
- `file-summary.csv`, `file-batches.csv` (batch manifests with method ranges), `coverage-by-class.csv`,
  `july-crosswalk.csv`, `findings/` (committed per-batch JSONL fragments), `mutations/` (committed
  patches + `mutations.csv`), `work/` (gitignored: TRX, cobertura XML, scratch, raw agent drafts).
- D3 proposals are committed as `coverage-opportunities.md` for review, then folded into the dated
  audit doc.

T7 register: a section of the dated audit doc; every **verified** T7 also gets an entry in
`docs/dev/todo-and-known-bugs.md` in the same commit, headed
`## TQ-<n>-<slug>: <prose> [FILED 2026-09-14 off test-quality-audit, evidence <file:line@4aedb0a>]`.

## Rubric (frozen before Phase 1; lives in `rubric.md`, embedded verbatim in every agent prompt)

**Verdict vocabulary** (per test method, D1 `verdict`):
`ok` (sound; a realistic regression reds it; no action), `weak`, `vacuous`, `duplicate`, `brittle`,
`misleading` (name/intent mismatch), `organization`, `source-gate-ok`, `source-gate-misplaced`,
`fixture-limited`, `uncertain` (needs second opinion; only value allowed for Tier D mechanical rows).

**Category mapping** (D2 `category`): T1 vacuous/cannot-fail, T2 redundant/duplicate, T3
weak/misleading, T4 brittle/flaky, T5 coverage opportunity, T6 organization, T7 production bug.

**Severity** (D2 `severity`): `Critical` = a realistic regression in save/sidecar/ledger/rewind
data-destruction or career-corruption paths leaves the suite green. `High` = same for recording
integrity/playback/chain/terminal-state correctness. `Medium` = wrong branch pinned, over-mocked,
misleading name, order-dependent flake risk, missing negative/mirror case. `Low` = organization,
naming, duplication cost only.

**Falsifiability format** (required for every non-`ok` finding): exact string
`falsifiability: <SUT file:line>; <production change> -> red`. Three exemptions that must be named
explicitly: `sut=test-infrastructure` (generator/codec tests), `sut=wiring-gate` (source-text gate,
legitimate per `design-testing-unified.md` §2), `sut=multi-line integration` (name the line set).

**Source-text gate acceptance rule**: a source-scan test is `source-gate-ok` unless it is the SOLE
coverage for behavior, or a behavioral test was feasible and skipped, or it regexes source text where
an AST walk is required (comments read as code and fail GREEN); only then `source-gate-misplaced`.
This prevents ~100 bogus T4s from the raw count.

**Confidence** (D2): `high|medium|low`, separate from severity. Verification status:
`unverified|verified|refuted`.

## Recon facts (at baseline SHA)

- 1,010 test `.cs` files, ~23.8 MB; 19,436 `[Fact]` + 799 `[Theory]` = 20,235 methods; 23,421
  executed cases in the TRX (23,420 passed, 1 skipped, 0 failed; 1m09s). 604 production files in
  `Source/Parsek` excluding `InGameTests/`.
- Size mix: 24 files <2 KB, 246 at 2-8 KB, 520 at 8-32 KB, 206 at 32-128 KB, 14 >128 KB.
- Structure: flat test classes, no inheritance, no xUnit fixtures, one collection definition;
  ~72% of files in `[Collection("Sequential")]`. Regex/script inventory is accurate at this shape.
- Shallow probes: 1 test-attribute file with zero `Assert.` (`Analyzer/RecordingSectionDump.cs`),
  0 `Assert.True(true)`, 1 `Skip=`, 0 empty bodies. Value is in semantic issues: e.g.
  `BugFixTests.cs` holds near-duplicate tests and tests that simulate a production guard inline.
- Compiler-surface findings already visible: xUnit2013 x4 (Assert.Equal used for collection size),
  xUnit2000 x1 (reversed `Assert.NotEqual` arguments in `RewindCrewLossFixtureTests`).
- Smell indicators to triage: 100 files reading production source text, 324 `Guid.NewGuid`,
  7 `DateTime.Now/UtcNow`, 2 `Thread.Sleep`, 77 reused method names (mostly helpers; test-method
  scoped counts come from the scanner).
- No mutation tooling exists (July audit D5); mutations are bounded, scripted and recorded here.

## Issue taxonomy

T1 vacuous/cannot-fail - tautology, asserts on a copy, no meaningful assert, inline simulation of
production logic, assertion weaker than the name.
T2 redundant/duplicate - same branch covered N times in-file or cross-file; redundancy is
agent-confirmed, never hash-only.
T3 weak/misleading - asserts implementation detail instead of contract, over-mocked, wrong tolerance,
name/intent mismatch, culture/format coupling.
T4 brittle/flaky - shared static state without `Sequential`/reset, order dependence, wall clock,
unbounded Guid assumptions, misplaced source-text needle.
T5 coverage opportunity - uncovered guard/branch, missing negative cases, missing mirror direction
(encode/decode, carrier/non-carrier), missing serialization keys.
T6 organization - mega-files, bug-number graveyard files, fixture duplication, generator ceilings.
T7 production bug found - separate triage; verified ones filed todo; never fixed inside this audit.

## Phases

### Phase 0 - baseline and instrumentation (mechanical)

- P0.1 Pin baseline SHA; audit worktree `../Parsek-test-audit` (branch `test-quality-audit`).
  Grandfathered path: this is the Phase A worktree only; all Phase B worktrees use `../Parsek-<branch>`.
- P0.2 One full `dotnet test` with TRX + `/p:CollectCoverage=true` (DONE, green). Full-suite runs are
  serialized machine-wide, one at a time; a red attributed to a concurrent sibling run is not a
  finding. `InjectAllRecordings` is excluded from machine-independent runs and run only explicitly
  (it writes the shared dev save; retry once on a lock flake).
- P0.3 Coverage parsed to `coverage-by-class.csv`; per-class stats marked `partial` if baseline is
  red. Fallback if coverlet.msbuild misbehaves: `--collect:"XPlat Code Coverage"` (coverlet.collector)
  or coverlet.console; coverage never gates the audit.
- P0.4 `inventory_scan.py` -> D1 seed; string/comment-aware body extraction, theory counting as
  `1 | inline:<n> | member:<provider> | rows:<n-after-TRX>`. `inline_data_total` sums Theory rows only.
- P0.5 `smell_sweep.py`, `redundancy.py` -> candidate findings with confidence. Redundancy rules:
  normalize = comments removed, whitespace collapsed, identifiers and string literals preserved,
  attributes excluded; exact tier = SHA1(normalized body), >=2 methods -> candidate; near tier =
  token-sequence similarity >=0.90 within cluster/size bucket, `needs-review`, capped 20 pairs per
  file. Exclusions: methods <5 statements, Theories with different InlineData counts, `*Wiring*` /
  `*SourceGate*` / `GrepAudit*` / `*Contract*` source-scan files, ctor/Dispose. Validate on 100
  sampled pairs; require false-positive rate <=10%; record in `work/redundancy-calibration.csv`.
  T2 findings are agent-confirmed, never hash-only.
- P0.6 `build_batches.py` -> cluster + tier + `file-batches.csv`. Batch budget: one pass reads at
  most 100 KB of test source plus one production file (<=150 KB total SUT excerpts). A1 >128 KB: one
  file or method range per pass. A2 32-128 KB: 1-2 files. B 8-32 KB: 4-6 files. C 2-8 KB: 8-12 files.
  D <2 KB: mechanical classification plus spot check.
- P0.7 Rubric freeze + calibration: `rubric.md` committed; 2 pilot batches (one A2, one B) reviewed
  independently by 3 agents each; require >=80% pairwise agreement on verdict and category; below
  threshold, adjudicate and re-pilot once.
- P0.8 `lint_fragments.py`: validates batch fragments against the manifest - every method in the
  slice present exactly once, enums valid, every non-`ok` verdict has a `falsifiability` string,
  `file:line` resolves at the pinned SHA, IDs unique. No finding enters D2 except through a linted
  fragment.
- P0 exit: baseline counts/wall recorded; coverage joined; D1 row count equals inventory count; all
  1,010 files clustered and batched; rubric calibrated; status line updated in the same commit.

### Phase 1 - full-file review (every file gets an agent pass; depth tiered)

- Agents run on Opus High (bulk reads), max 8 in parallel per round; Fable (max 2) is reserved for
  calibration adjudication, Phase 2 refutations and Phase 4 synthesis. Round = 8 batches; ~24 rounds
  at the corrected budget (planning number 190 passes; range 160-260). Clusters are ordered
  risk-first: `rewind-refly`, `recording-tree`, `ledger-career`, `recorder-events`, `logistics-route`,
  `spawn-vessel`, `ghost-playback`, `map-render`, `trajectory-orbit`, `mission-groups`, `analyzer`,
  `logging`, `harness-seam`, `wiring-gates`, `ui-settings`, `io-serialization`, `legacy-bugfix`,
  `catchall`.
- Per batch, the agent writes `<cluster>-<batch>.jsonl` (one record per method plus finding records)
  under `findings/` and returns only a summary. Agents do not run git, do not commit, and write only
  under the audit worktree. All artifacts are plain ASCII, no em dashes, no emoji.
- Per-round metrics in `work/metrics.md`: methods reviewed, rows rejected by the linter, findings by
  severity, confirmed T1/T2 counts.
- Stop conditions: (a) linter rejects >20% of a round's fragments -> halt, fix the prompt, re-run the
  round; (b) two consecutive rounds with no Critical and T1+T2 below 5 confirmed per 1,000 reviewed
  methods -> switch B/C tiers to scanner-only triage and close Phase 1; (c) hard cap 200 passes or any
  round over two hours wall.
- P1 exit: every file has a linted fragment; 100% row coverage; every finding carries falsifiability;
  supervisor spot-check (10% of Medium, 100% of Critical) sampled and passed.

### Phase 2 - adversarial verification of findings

- Every Critical/High candidate: a second agent tries to refute it, reading the SUT; where cheap,
  a bounded mutation proves or refutes it. Per-category recipe: T1 = stub or negate the production
  line the test's name claims; GREEN means the test cannot fail (finding holds). T2 = one mutation,
  record which of the pair reds (both red = duplicate). T3 = a realistic semantic change (sign flip,
  boundary shift) staying green. T4 = rerun evidence, no mutation. Cap 30 mutations total.
- Mutations run ONLY in a detached scratch worktree `../Parsek-testaudit-mut` at the pinned SHA
  (`git worktree add --detach`); `git status --porcelain` must be empty before each run and after
  `git restore`. Never in `../Parsek-test-audit`, never committed. Record per mutation:
  `finding_id, sha, file, symbol, mutation_kind, patch` (`mutations/<id>.patch`), `baseline_result`
  (PASS for the filtered class), exact command
  (`dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~<Class>"`),
  `observed`, `red_test`, `restore_command`, `restored_hash`; row in `mutations.csv`.
- Supervisor spot-checks 10% of Medium findings and 100% of Critical. Refuted findings are recorded
  with the refutation and closed; they are not re-litigated.
- P2 exit: 100% of Critical/High have a recorded refutation attempt; refuted items labeled.

### Phase 3 - coverage-opportunity analysis (per high-value SUT)

- Candidates come from (a) coverage classes with uncovered guards in decision-dense SUTs, (b) D2
  T3/T5 findings, (c) the July crosswalk's open items. Each proposal cites the SUT guard `file:line`,
  the risk class and the mutant that would red the proposed test; cap 10 per cluster; rank by risk
  order (data loss > career/ledger > recording integrity > playback > UI/cosmetic), severity, effort.
- `case_kind ∈ {negative, mirror-direction, roundtrip, boundary, serializer-key, state-transition}`;
  each proposal carries `dupe_of_july_id` or `no-july-equivalent`.
- P3 exit: coverage-opportunities.md reviewed by supervisor; every claim spot-checked for feasibility.

### Phase 4 - synthesis and operator gate

- Dedup, rank, publish the dated audit doc D2/D3/D4; state counts by category, top-20 lists, Phase-B
  workstream assignment (each D2/D3 ID assigned to exactly one workstream). Implementation starts
  only on explicit operator go-ahead, recorded by date in the status line.
- Doc interactions (same commit as the audit doc): add a doc-map row to `autotest-status.md` naming
  this audit as an xUnit-quality SNAPSHOT (a dated audit, not a living status doc, superseding
  `test-coverage-audit-2026-07-29.md` only for xUnit-internal quality); add a pointer in
  `test-coverage-audit-2026-07-29.md`; amend `design-testing-unified.md`'s derivation line to name
  both audits. `july-crosswalk.csv` maps every July register ID (A/B/C/D/Tier E) to the SUT/file it
  names; any D2/D3 item matching a July entry carries `july_ref`; an open July item keeps the July ID
  as primary and only adds new evidence. Edit `autotest-status.md` additively: `test_doc_spec_sync.py`
  regex-pins B1/B13/B14 sentences.
- P4 exit: operator go-ahead recorded; Phase-B workstream assignment committed.

## Phase B - implementation (outline, starts only after go-ahead)

- Worktrees/branches (path always `../Parsek-<branch>`): `testfix-t1t2` -> `../Parsek-testfix-t1t2`;
  `testfix-t4-flaky`; `testfix-t5-coverage`; `testfix-t7-prodbugs`. Base: `origin/main` after this
  PR merges; a workstream may stack on `test-quality-audit` before that. One PR per workstream, never
  a local merge.
- Only the assigned workstream edits its own D2/D3 rows; conflicts in the audit doc resolve
  entry-level keep-both. Each agent prompt names its own worktree; never edit a sibling worktree.
- Every added or strengthened test carries a mutation proof: stub/invert the guarded production line,
  or invert the expected value / drop a serialized key / mutate the generator; where no mutant exists,
  the PR body states why. Mutations follow the Phase 2 rules (scratch worktree, patches committed).
- Full-suite green before each PR, run in the one suite slot; record in the PR body that it ran alone.
- Wave order: (1) T1/T2 fixes, (2) T4 flakiness, (3) T5 coverage additions, (4) T7 production bugs as
  separate PRs.
- Docs per commit: Phase A -> D1-D4 only. Phase B test-only -> audit-doc status + `autotest-status.md`
  xUnit counts if changed. T7 fix -> `CHANGELOG.md` + `todo-and-known-bugs.md` (mark entry fixed) +
  `autotest-status.md` if counts move, re-reading existing entries before follow-up pushes.
- Lifecycle: when Phase B closes, move the plan to `docs/dev/done/plans/` and the research dir to
  `docs/dev/done/research/`, and mark the audit doc historical in the doc map.

## Controls and risks

- No production or test edits during Phase A; mutations only in the detached scratch worktree.
- CI: every push to the audit PR and Phase B PRs pays the full `tests` suite (no paths filter; up to
  ~30 min; `tests` is required). Run `cd harness && python -m unittest discover -s lib -q` locally
  before any commit that edits docs pinned by `test_doc_spec_sync.py`.
- Coverage is a diagnostic, never a target; the output is a ranked backlog, not a mandate.
- Repeat full-suite runs only at phase gates; use `--filter` per class during review.
