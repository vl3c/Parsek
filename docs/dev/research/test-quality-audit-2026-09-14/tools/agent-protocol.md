# Phase 1 review-agent protocol (identical for every pass)

You are reviewing xUnit tests for the Parsek unit-test quality audit. Work only inside
`C:\Users\vlad3\Documents\Code\Parsek\Parsek-test-audit` (the audit worktree). Never run git, never run
tests, never edit production or test code, never touch `Source/Parsek.Tests` files - read only.

## Inputs

- The batch ids assigned to you, each with a manifest at
  `docs/dev/research/test-quality-audit-2026-09-14/work/manifests/<batch_id>.json`.
  The manifest lists the files, method ranges and the exact methods you must return rows for.
- The frozen rubric: `docs/dev/research/test-quality-audit-2026-09-14/rubric.md`. Read it first.
- Primary SUT sources on demand under `Source/Parsek` (read only the parts you need; budget: at most
  one production file of excerpts per pass).

## Output

For EACH batch, write `docs/dev/research/test-quality-audit-2026-09-14/findings/<batch_id>.jsonl`
with one JSON object per line, exactly these shapes:

Method row (one per method listed in the manifest, no more, no fewer):
{"kind":"method","file":"Source/Parsek.Tests/...","method":"Name","line":123,"verdict":"ok"}
{"kind":"method","file":"...","method":"Name","line":123,"verdict":"weak","category":"T3","falsifiability":"falsifiability: Source/Parsek/Foo.cs:42; invert the guard -> red","note":"short"}

Finding row (optional, for every non-ok verdict that needs action):
{"kind":"finding","id":"F-<batch>-01","file":"...","line":123,"method":"Name","category":"T1","severity":"High","confidence":"high","falsifiability":"falsifiability: ...","what_it_asserts":"...","why_weak":"...","proposed_action":"...","effort":"S","july_ref":"none"}

Coverage candidate row (optional):
{"kind":"coverage","sut":"TypeName","sut_file":"Source/Parsek/Foo.cs","sut_line":42,"case_kind":"negative","proposed_name":"Foo_OnNull_ReturnsFalse","sketch":"arrange/act/assert sketch","guards":"what regression it catches","risk":"recording"}

## Verdict guidance

- `ok` needs no prose. Be honest: most tests in this suite are expected to be ok. Do not invent
  findings to fill a quota. A pass with zero findings is a valid result.
- Non-ok verdicts always carry `category` and `falsifiability`. `duplicate` also carries
  `twin: <file>.<method>`.
- Findings must be mechanical and reproducible: a reviewer at the pinned SHA must agree without
  running code, or with the stated mutation. Style-only opinions are not findings.
- When a test is sound but only because a fixture/generator ceiling blocks a stronger case, use
  `fixture-limited` and file a coverage candidate for the generator change.
- If you genuinely cannot decide without runtime evidence, use `uncertain` and say what evidence
  would decide it. Do not guess.

## Reading discipline

- Read the batch's test files completely (ranges tell you where they start/end in split files).
- For a test whose SUT is unclear from the name, read only the minimal production excerpt needed to
  judge falsifiability (for example, the method under test and its immediate guards).
- Prior stack-level audit for context (do not duplicate its gap register):
  `docs/dev/test-coverage-audit-2026-07-29.md`.

## Self-check before returning

Run the linter for each of your batches and fix every ERR it reports:

python docs/dev/research/test-quality-audit-2026-09-14/tools/lint_fragments.py \
  --manifests docs/dev/research/test-quality-audit-2026-09-14/work/manifests \
  --fragments docs/dev/research/test-quality-audit-2026-09-14/findings \
  --batch <id> --batch <id2>

Paste the PASS lines into your summary. A batch whose fragment FAILs lint is not done.

## Return summary (to the supervisor)

Return at most 12 lines: batch ids; method rows written; counts by verdict; findings by
severity/category; coverage candidates; anything that blocked review (missing file, ambiguous SUT).
No restatement of the rubric, no prose beyond that.
