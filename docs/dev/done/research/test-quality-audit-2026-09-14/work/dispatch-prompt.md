You are a Phase 1 review agent for the Parsek unit-test quality audit.

Audit worktree root (the ONLY tree you may read or write): C:\Users\vlad3\Documents\Code\Parsek\Parsek-test-audit
Never read under C:\Users\vlad3\Documents\Code\Parsek\Parsek (main checkout) or C:\Users\vlad3\Documents\Code\Parsek\docs.
Never run git, never run dotnet test, never edit any .cs file. Read-only except your own fragment files.

Step 1: read, in full, these two files (absolute paths):
  C:\Users\vlad3\Documents\Code\Parsek\Parsek-test-audit\docs\dev\research\test-quality-audit-2026-09-14\tools\agent-protocol.md
  C:\Users\vlad3\Documents\Code\Parsek\Parsek-test-audit\docs\dev\research\test-quality-audit-2026-09-14\rubric.md
Follow them exactly. They define the JSONL row shapes, verdict enums, the falsifiability string
(must contain "->"), and the linter self-check.

Step 2: process your batches ONE AT A TIME, in the order listed. For each batch: open its manifest
under ...\work\manifests\<batch_id>.json, read the listed test files (Source\Parsek.Tests\<file>,
the listed line ranges), read the minimal production excerpt needed for falsifiability, write
...\findings\<batch_id>.jsonl (one method row per manifest method, exactly; plus finding/coverage
rows where warranted), then run the linter for that batch and fix every ERR before starting the
next batch. Write the fragment file with a plain UTF-8 write; ASCII only, no em dashes, no emoji.

Calibration reminders: most tests are ok; do not invent findings. weak vs vacuous: if stubbing the
production line the test names leaves the test green, it is vacuous. duplicate rows need "twin:".
Falsifiability strings must contain "->" (e.g. "-> red", "-> both red", "-> green").

Step 3: return at most 12 lines: batch ids; method rows written per batch; counts by verdict;
findings by severity/category; coverage candidates; blockers. Paste the linter PASS line for every
batch. Do not return prose beyond that.

Your batches (process in this order):
