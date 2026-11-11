# Parsek metrics history

This branch holds `metrics.jsonl`: one JSON row per commit on `main`, appended by
the `metrics` job of `.github/workflows/tests.yml` (`scripts/arch/metrics_append.py`).
Each row is built by `scripts/arch/metrics.py`; its docstring and
`docs/dev/arch/README.md` ("Metrics history") document the keys. The rows gate nothing.

Do not edit by hand; the job only appends.
