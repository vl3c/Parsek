# Parsek harness run index

This folder holds the harness run index (the flight results; the unit-test metrics are
`metrics.jsonl` at the branch root), written by `harness/tools/run_index.py publish`
from the machine-wide results archive (`<umbrella>/automation/results-archive/`):

- `index.jsonl`: one JSON line per archived run attempt (schema `parsek-run-index/1`).
- `lane-health.json`: per lane, from its latest PASS, Parsek log lines per subsystem
  and per module (schema `parsek-lane-health/1`).

`harness/lib/runindex.py` documents every key. Read it with
`python harness/tools/run_index.py fetch --repo . --remote origin --branch metrics --out index.jsonl`
and then `run_index.py lane <LANE> --index index.jsonl`. Publishing merges by run id
(and by lane), so two machines may both publish. Each publish is one commit whose
subject starts with `flights: `, so `git log -- flights/` is the flight history alone.
Publishing writes only the three files of this folder; every other file on the branch
is kept. Do not edit these three by hand.
