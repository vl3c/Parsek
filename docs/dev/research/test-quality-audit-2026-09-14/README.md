# test-quality-audit-2026-09-14 - research directory layout

Baseline `4aedb0a1a`. Headline document: `docs/dev/test-quality-audit-2026-09-14.md` (its appendix
carries the full file map). Plan and phase status line: `docs/dev/plans/test-quality-audit.md`.

Committed, top level:

- `rubric.md` (frozen review rubric), `calibration-2026-09-14.md` (pilot agreement),
  `phase1-summary.md`, `phase2-summary.md`, `coverage-opportunities.md` (D3 narrative).
- `test-inventory.csv` (D1, one row per test method, written only by `tools/merge_fragments.py`),
  `findings.csv` and `issue-register.csv` (D2; the register is the same rows sorted by severity),
  `coverage-opportunities.csv` (D3 with triage columns), `phase2-verdicts.csv`,
  `july-crosswalk.csv`, `file-summary.csv`, `file-batches.csv`, `coverage-by-class.csv`.
- `findings/` - one linted JSONL fragment per batch (426 manifest batches + `supplement-001`);
  `findings/pilot/` holds the calibration runs and is NOT merged.
- `mutations/` - Phase 2 patches and `mutations.csv` (one row per mutation run).
- `tools/` - stdlib Python: `inventory_scan.py`, `parse_results.py`, `build_batches.py`,
  `make_workorders.py`, `lint_fragments.py`, `merge_fragments.py`, and the agent protocol
  `agent-protocol.md`.

Committed, under `work/` (small, needed to re-run or trace):

- `work/manifests/<batch>.json` - the per-batch method lists the linter checks fragments against.
- `work/workorders.txt`, `work/dispatch-prompt.md` - the Phase 1 dispatch order and the prompt
  every review agent received (with `tools/agent-protocol.md`).
- `work/phase2/` - the High set, the seeded Medium sample (three files), and the per-agent verdicts.
- `work/phase3/` - the candidate partitions, `README-agent.md` (triage instructions) and the
  per-partition ranked outputs (`ranked-*.jsonl` / `.md`).
- `work/phase4/july-refs.jsonl` - the finding/candidate -> July ID pairs folded into the CSVs.
- `work/supervisor-notes.md`, `work/metrics.md`, `work/inventory-completeness.txt`,
  `work/trx-summary.txt`, `work/non-test-files.txt`.

Ignored (large, regenerable): `work/baseline.trx`, `work/coverage.cobertura.xml`,
`work/durations.csv`, `work/batches.json`, `work/test-inventory-seed.csv`.

Regenerate:

    python tools/lint_fragments.py --manifests work/manifests --fragments findings --all
    python tools/merge_fragments.py --inventory test-inventory.csv --fragments findings --out .

(`merge_fragments.py` rewrites only the `verdict` / `register_id` columns, so the committed
`test-inventory.csv` is a valid input to itself; the seed CSV is not required.)
