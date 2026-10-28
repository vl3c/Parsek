#!/usr/bin/env python3
"""Offline ledger audit of one run: what the career economy did, from KSP.log.

Thin I/O shell over ``harness/lib/ledgeraudit.py`` (every decision lives there):
finds the run's KSP.log (plain or ``KSP.log.gz``) and any saved
``Parsek/GameState/ledger.pgld`` under the run folder, reads an optional per-lane
expectations file and an optional baseline log, writes ``audit.txt`` into the
run folder (or ``--out``) and prints it.

It only READS run artifacts; no flight, no provisioning, no KSP.

Usage::

    python harness/tools/ledger_audit.py <run-folder-or-KSP.log>
        [--expect FILE] [--baseline PATH] [--out FILE] [--no-write]

Expectations (TOML, or JSON when the file ends in ``.json``)::

    expected = ["take-back"]                # take-back | debt | not-affordable | refly-leftover
    expectedLines = ["NOT affordable"]      # literals; mark matching expectable lines EXPECTED
    maxStructuralPerMinute = 30             # arms recalc-rate
    mergeMustPass = true                    # arms merge-refused

Exit code: 0 when no check FAILs, 1 when one does, 2 on a usage error (no log,
an unreadable or invalid expectations file).
"""

from __future__ import annotations

import argparse
import gzip
import json
import os
import sys
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_DIR = os.path.dirname(_HERE)
_LIB_DIR = os.path.join(_HARNESS_DIR, "lib")
if _LIB_DIR not in sys.path:
    sys.path.insert(0, _LIB_DIR)

import ledgeraudit  # noqa: E402

try:
    import tomllib  # Python 3.11+
except ImportError:  # pragma: no cover - the harness itself requires 3.11
    tomllib = None

LOG_NAMES = ("KSP.log", "KSP.log.gz")
LEDGER_RELPATH = os.path.join("Parsek", "GameState", "ledger.pgld")
# A run folder holds the log at its top or one level down (collect-logs output,
# a `_shots` dir); deeper hits are someone else's snapshot.
LOG_SEARCH_DEPTH = 2


class UsageError(Exception):
    pass


def read_log_lines(path: str) -> List[str]:
    opener = gzip.open if path.endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8", errors="replace") as fh:
        return fh.read().splitlines()


def _depth(root: str, path: str) -> int:
    rel = os.path.relpath(path, root)
    return 0 if rel == "." else rel.count(os.sep) + 1


def find_log(target: str) -> str:
    if os.path.isfile(target):
        return target
    if not os.path.isdir(target):
        raise UsageError("no such run folder or log: %s" % target)
    for name in LOG_NAMES:
        p = os.path.join(target, name)
        if os.path.isfile(p):
            return p
    hits = []
    for dirpath, dirnames, filenames in os.walk(target):
        if _depth(target, dirpath) >= LOG_SEARCH_DEPTH:
            dirnames[:] = []
        for name in LOG_NAMES:
            if name in filenames:
                hits.append(os.path.join(dirpath, name))
        dirnames.sort()
    if not hits:
        raise UsageError("no KSP.log or KSP.log.gz in %s" % target)
    return sorted(hits)[0]


def find_ledgers(run_dir: Optional[str]) -> List[Tuple[str, str]]:
    if not run_dir or not os.path.isdir(run_dir):
        return []
    out = []
    for dirpath, dirnames, filenames in os.walk(run_dir):
        dirnames.sort()
        if "ledger.pgld" in filenames and dirpath.endswith(os.path.dirname(LEDGER_RELPATH)):
            p = os.path.join(dirpath, "ledger.pgld")
            try:
                with open(p, "r", encoding="utf-8", errors="replace") as fh:
                    out.append((os.path.relpath(p, run_dir), fh.read()))
            except OSError:
                continue
    return out


def load_expectations(path: Optional[str]) -> ledgeraudit.Expectations:
    if not path:
        return ledgeraudit.Expectations()
    try:
        if path.endswith(".json"):
            with open(path, "r", encoding="utf-8") as fh:
                obj = json.load(fh)
        else:
            if tomllib is None:
                raise UsageError("TOML expectations need Python 3.11+ (tomllib); use a .json file")
            with open(path, "rb") as fh:
                obj = tomllib.load(fh)
        return ledgeraudit.parse_expectations(obj, source=path)
    except (OSError, ValueError) as exc:
        raise UsageError("expectations %s: %s" % (path, exc))


def run(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("target", help="run folder or a KSP.log / KSP.log.gz")
    ap.add_argument("--expect", help="per-lane expectations file (TOML, or .json)")
    ap.add_argument("--baseline", help="another run folder or log to diff the WARN/ERROR keys against")
    ap.add_argument("--out", help="write the report here instead of <run folder>/audit.txt")
    ap.add_argument("--no-write", action="store_true", help="print only")
    args = ap.parse_args(argv)
    try:
        log_path = find_log(args.target)
        exp = load_expectations(args.expect)
        baseline = read_log_lines(find_log(args.baseline)) if args.baseline else None
        lines = read_log_lines(log_path)
    except (UsageError, OSError) as exc:
        print("ledger_audit: %s" % exc, file=sys.stderr)
        return 2
    run_dir = args.target if os.path.isdir(args.target) else None
    result = ledgeraudit.audit(lines, exp, baseline, find_ledgers(run_dir), log_label=log_path)
    text = ledgeraudit.render(result)
    sys.stdout.write(text)
    if not args.no_write:
        out = args.out or os.path.join(run_dir or os.path.dirname(os.path.abspath(log_path)),
                                       "audit.txt")
        try:
            with open(out, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(text)
        except OSError as exc:
            print("ledger_audit: cannot write %s: %s" % (out, exc), file=sys.stderr)
            return 2
    return ledgeraudit.exit_code(result)


if __name__ == "__main__":
    sys.exit(run())
