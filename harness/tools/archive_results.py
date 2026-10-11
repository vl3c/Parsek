#!/usr/bin/env python3
"""Back-fill the machine-wide harness results archive from an existing results dir.

run.py archives every new run into ``<umbrella>/automation/results-archive/<runId>/``
on its own (HARNESS-RESULTS-ARCHIVE). This one-shot tool imports runs flown
before that, or a surviving snapshot of some worktree's ``harness/results``,
through the SAME pure plan (``hlib.plan_results_archive``) and the same copy
(``run.archive_run_results``). Each run's harness log is the invocation log
that recorded ``result written .../<runId>.json``.

Usage::

    python harness/tools/archive_results.py --from "C:/.../Parsek-x/harness/results"
        [--from DIR ...] [--umbrella-root DIR | --archive-dir DIR]
        [--since YYYY-MM-DD] [--overwrite] [--dry-run]

Runs already in the archive are left alone unless ``--overwrite``. It never
deletes anything from ``--from``. The opt-out env var does not apply here: the
tool only runs when asked.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from typing import Dict, List, Optional

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_DIR = os.path.dirname(_HERE)
_LIB_DIR = os.path.join(_HARNESS_DIR, "lib")
for _p in (_LIB_DIR, _HARNESS_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import run  # noqa: E402

_HARNESS_LOG_RE = re.compile(r"^\d{4}-\d{2}-\d{2}_\d{6}_harness\.log$")


class _PrintLogger:
    def __init__(self):
        self.warnings = 0

    def _out(self, level, step, msg):
        print(hlib.format_log_line(level, step, msg))

    def info(self, step, msg):
        self._out("Info", step, msg)

    def verbose(self, step, msg):
        self._out("Verbose", step, msg)

    def warn(self, step, msg):
        self.warnings += 1
        self._out("Warn", step, msg)

    def error(self, step, msg):
        self._out("Error", step, msg)


def result_run_ids(names: List[str]) -> List[str]:
    """Run ids that own a ``<runId>.json`` result in a results-dir listing."""
    present = set(names)
    return sorted(rid for rid in hlib.claimed_run_ids(names) if rid + ".json" in present)


def _read_harness_logs(results_dir: str, names: List[str]) -> Dict[str, str]:
    out: Dict[str, str] = {}
    for name in names:
        if not _HARNESS_LOG_RE.match(name):
            continue
        try:
            with open(os.path.join(results_dir, name), "r", encoding="utf-8",
                      errors="replace") as fh:
                # Only the result-write lines matter for the mapping.
                out[name] = "".join(ln for ln in fh if "result written" in ln)
        except OSError:
            continue
    return out


def _worktree_of(results_dir: str) -> str:
    """``<worktree>/harness/results`` -> ``<worktree>``; anything else is itself."""
    norm = os.path.normpath(os.path.abspath(results_dir))
    parent = os.path.dirname(norm)
    if (os.path.basename(norm) == "results"
            and os.path.basename(parent) == "harness"):
        return os.path.dirname(parent)
    return norm


def backfill(results_dir: str, archive_root: str, logger, since: Optional[str] = None,
             overwrite: bool = False, dry_run: bool = False) -> Dict[str, int]:
    counts = {"archived": 0, "existing": 0, "failed": 0, "planned": 0}
    try:
        names = sorted(os.listdir(results_dir))
    except OSError as exc:
        logger.warn("Backfill", "cannot list %s (%s)" % (results_dir, exc))
        return counts
    run_ids = [r for r in result_run_ids(names) if not since or r[:10] >= since]
    log_by_run = hlib.harness_log_names_for_runs(_read_harness_logs(results_dir, names),
                                                 run_ids)
    worktree = _worktree_of(results_dir)
    for rid in run_ids:
        if not overwrite and os.path.isdir(os.path.join(archive_root, rid)):
            counts["existing"] += 1
            continue
        counts["planned"] += 1
        if dry_run:
            logger.info("Backfill", "would archive run=%s harnessLog=%s"
                        % (rid, log_by_run.get(rid)))
            continue
        log_name = log_by_run.get(rid)
        log_path = os.path.join(results_dir, log_name) if log_name else None
        if run.archive_run_results(rid, results_dir, archive_root, log_path, logger,
                                   worktree=worktree, origin="backfill",
                                   record_git_head=False):
            counts["archived"] += 1
        else:
            counts["failed"] += 1
    logger.info("Backfill", "from=%s runs=%d archived=%d alreadyArchived=%d failed=%d%s"
                % (results_dir, len(run_ids), counts["archived"], counts["existing"],
                   counts["failed"], " (dry run)" if dry_run else ""))
    return counts


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="Back-fill the harness results archive")
    ap.add_argument("--from", dest="sources", action="append", required=True,
                    help="a results dir to import (repeatable)")
    ap.add_argument("--umbrella-root", default=None,
                    help="umbrella root (default: parent of this worktree)")
    ap.add_argument("--archive-dir", default=None,
                    help="archive root override (default: <umbrella>/automation/results-archive)")
    ap.add_argument("--since", default=None, help="only run ids dated >= YYYY-MM-DD")
    ap.add_argument("--overwrite", action="store_true",
                    help="re-archive runs already in the archive")
    ap.add_argument("--dry-run", action="store_true", help="list what would be archived")
    args = ap.parse_args(argv)

    if args.archive_dir:
        archive_root = os.path.abspath(args.archive_dir)
    else:
        umbrella = os.path.abspath(args.umbrella_root or run.DEFAULT_UMBRELLA_ROOT)
        archive_root = run.results_archive_root(umbrella)
    logger = _PrintLogger()
    failed = 0
    for src in args.sources:
        failed += backfill(src, archive_root, logger, args.since, args.overwrite,
                           args.dry_run)["failed"]
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
