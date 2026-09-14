# Test-quality audit rubric (frozen 2026-09-14, baseline 4aedb0a)

This rubric is embedded in every Phase 1 review agent prompt. Do not edit mid-phase; changes
require a dated amendment note at the bottom and a supervisor adjudication of already-reviewed rows.

## Verdict vocabulary (one per test method)

- `ok` - sound. A realistic regression in the code it exercises reds it; no action.
- `weak` - it can fail, but only on a narrow or unlikely change; a stronger assertion is feasible.
- `vacuous` - it cannot fail for the reason its name claims (tautology, asserts on a copy, asserts a
  mock it configured, replays production logic inline, or has no meaningful assertion).
- `duplicate` - another test (name the twin) exercises the same behavior with the same assertions;
  one should go or be merged.
- `brittle` - order/static-state/timing/culture/Guid dependent, or otherwise liable to flake.
- `misleading` - the name or comment claims more/different than the assertions prove.
- `organization` - placement/naming/structure issue only (mega-file, bug-number file); behavior sound.
- `source-gate-ok` - asserts on production source text but legitimately (wiring gate per
  `design-testing-unified.md` §2, or the only cheap mechanical way to pin a wiring fact).
- `source-gate-misplaced` - source-text assertion where a behavioral test is feasible and absent, or
  regex-over-source where an AST walk is required (comments read as code and fail GREEN).
- `fixture-limited` - the test is sound but constrained by a fixture/generator ceiling; note the
  ceiling so Phase 3 can propose the generator change.
- `uncertain` - cannot decide without runtime evidence; allowed only when the note says what evidence
  would decide it.

Severity and category apply to FINDINGS, not to `ok` rows.

## Verdict boundary: weak vs vacuous (calibration 2026-09-14)

If stubbing or removing the production line the test names would leave the test green (because the
test re-issues the call inline, asserts a helper result, or never reaches the line), the verdict is
`vacuous`, not `weak`. `weak` means the production line IS exercised but the assertion window is
narrow (single branch, loose tolerance, no adjacent negative case).

## Finding categories

- T1 vacuous/cannot-fail, T2 redundant/duplicate, T3 weak/misleading, T4 brittle/flaky,
  T5 coverage opportunity, T6 organization, T7 production bug (file separately, never fix here).

## Severity

- `Critical` - a realistic regression in save/sidecar/ledger/rewind data-destruction or
  career-corruption paths leaves the suite green.
- `High` - same for recording integrity/playback/chain/terminal-state correctness.
- `Medium` - wrong branch pinned, over-mocked, misleading name, order-dependent flake risk,
  missing negative/mirror case.
- `Low` - organization, naming, duplication cost only.

## Falsifiability (mandatory for every non-ok verdict and every finding)

Exact shape: `falsifiability: <SUT file:line>; <the production change> -> red`
(the change listed must make THIS test fail). One of three exemptions must be named when applicable:
`sut=test-infrastructure` (generator/codec tests), `sut=wiring-gate` (source-text gate),
`sut=multi-line integration` (name the line set). A finding without a falsifiability string is
dropped by the linter, not adjudicated.

## Redundancy rule

`duplicate` requires naming the twin as `twin: <file>.<method>`. Hash/near-match candidates from
`tools/redundancy.py` are hints only; the agent must confirm the twin asserts the same behavior.
Theories differing only in data rows are not duplicates. Two tests pinning the same branch with
different upstream setup are not duplicates unless they assert the same observable.

## Source-text gate acceptance rule

Source-scan tests are `source-gate-ok` unless (a) they are the SOLE coverage for their SUT behavior,
(b) a behavioral test was feasible and skipped, or (c) they regex source text where an AST walk is
required. Only then `source-gate-misplaced` (T4). Do not flood the register with source-scan files.

## Evidence rules

- Cite `file:line` at the pinned SHA for every claim.
- Review agents never run tests and never run git.
- Only write to your assigned `findings/<batch>.jsonl`.
- Plain ASCII only, no em dashes, no emoji. Notes are short (<200 chars), factual, no prose essays.
