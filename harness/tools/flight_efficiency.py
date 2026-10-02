#!/usr/bin/env python3
"""Real-time (wall-clock) efficiency of harness auto-flights.

Thin I/O shell over ``harness/lib/flighteff.py`` (every decision lives there):
finds runs in one or more ``harness/results`` dirs, reads each run's artifacts,
and prints where the wall time went -- idle 1x seconds by cause, rails warp below
the altitude-legal factor, physics warp (its own bucket, never recoverable), long
1x burns, and the harness overhead outside the mission (KSP boot, seam steps,
quit, the post-quit verifier tail) -- with an estimated recoverable figure and
the code site that controls each. 1x time the mission warp policy keeps on
purpose is reported as by-design, beside the recoverable total, never in it.

It only MEASURES: it reads run artifacts and writes nothing but stdout and the
optional ``--json-out`` file. No flight, no provisioning, no KSP.

Usage::

    python harness/tools/flight_efficiency.py                       # this worktree's results
    python harness/tools/flight_efficiency.py --run-id 2026-10-01_1231_B11-mun-orbit
    python harness/tools/flight_efficiency.py \\
        --results-glob "C:/.../Parsek-*/harness/results" --since 2026-10-01 \\
        [--scenario B11-mun-orbit] [--per-run] [--top 10] [--json-out eff.json]

Runs that appear in several worktrees are deduplicated by run id (the copy with
the most artifacts wins). The estimation contract is the flighteff module
docstring; ``harness/README.md`` summarises it.
"""

from __future__ import annotations

import argparse
import glob
import json
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

import flighteff  # noqa: E402

# Longest suffix first: "_mission.json" must win over ".json".
_SUFFIXES = (
    ("_mission.stdout.log", "missionLog"),
    ("_mission.json", "missionJson"),
    ("_status.json", "status"),
    ("_shots", "shots"),
    (".json", "result"),
)
_HARNESS_LOG_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})_\d{6}_harness\.log$")


def _read_text(path: str) -> Optional[str]:
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return None


def _read_json(path: str):
    text = _read_text(path)
    if text is None:
        return None
    try:
        return json.loads(text)
    except ValueError:
        return None


def discover(results_dir: str) -> List[Dict]:
    """Candidate runs in one results dir (paths only; nothing parsed yet)."""
    try:
        names = os.listdir(results_dir)
    except OSError:
        return []
    by_id: Dict[str, Dict] = {}
    for name in names:
        for suffix, kind in _SUFFIXES:
            if not name.endswith(suffix):
                continue
            stem = name[:-len(suffix)]
            if not flighteff.RUN_ID_RE.match(stem):
                break
            c = by_id.setdefault(stem, {"runId": stem, "dir": results_dir})
            c[kind] = os.path.join(results_dir, name)
            break
    for c in by_id.values():
        shots = c.get("shots")
        if shots:
            ksp = os.path.join(shots, "KSP.log")
            if os.path.isfile(ksp):
                c["kspLog"] = ksp
    return list(by_id.values())


def harness_facts(results_dir: str, since: Optional[str]) -> Dict[str, Dict]:
    """runId -> lock / retry / cost facts from every run.py log in the dir."""
    out: Dict[str, Dict] = {}
    try:
        names = sorted(os.listdir(results_dir))
    except OSError:
        return out
    for name in names:
        m = _HARNESS_LOG_RE.match(name)
        if not m or (since and m.group(1) < since):
            continue
        path = os.path.join(results_dir, name)
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                lines = [ln for ln in f if "[Harness]" in ln and (
                    "[Lock]" in ln or "[Result]" in ln or "[Retry]" in ln or "[Cost]" in ln)]
        except OSError:
            continue
        for rid, facts in flighteff.parse_harness_log(lines).items():
            facts["harnessLog"] = path
            out[rid] = facts
    return out


def collect(dirs: List[str], since: Optional[str], scenarios: List[str],
            run_ids: List[str]) -> Dict:
    cands: List[Dict] = []
    facts: Dict[str, Dict] = {}
    for d in dirs:
        for c in discover(d):
            rid = c["runId"]
            if since and rid[:10] < since:
                continue
            if run_ids and rid not in run_ids:
                continue
            if scenarios and flighteff.scenario_from_run_id(rid) not in scenarios:
                continue
            cands.append(c)
        for rid, f in harness_facts(d, since).items():
            facts.setdefault(rid, f)
    kept, dupes = flighteff.dedupe_candidates(cands)
    runs: List[Dict] = []
    skipped: List[Dict] = []
    for c in kept:
        rid = c["runId"]
        if not c.get("result") and not c.get("missionLog"):
            skipped.append({"runId": rid, "dir": c["dir"],
                            "reason": "no result json and no mission log"})
            continue
        if not c.get("missionLog") and not c.get("kspLog"):
            skipped.append({"runId": rid, "dir": c["dir"],
                            "reason": "no mission log and no KSP.log"})
            continue
        notes: List[str] = []
        result = _read_json(c["result"]) if c.get("result") else None
        if c.get("result") and not isinstance(result, dict):
            notes.append("result json unreadable")
            result = None
        if result is None:
            notes.append("no result json: overhead rows unknown")
        mjson = _read_json(c["missionJson"]) if c.get("missionJson") else None
        if c.get("missionJson") and not isinstance(mjson, dict):
            notes.append("mission.json unreadable")
            mjson = None
        mlog = _read_text(c["missionLog"]) if c.get("missionLog") else None
        ksp_lines = None
        if c.get("kspLog"):
            text = _read_text(c["kspLog"])
            ksp_lines = text.splitlines() if text is not None else None
        try:
            run = flighteff.analyze_run(rid, result, mjson, mlog, ksp_lines,
                                        facts.get(rid), notes)
        except Exception as exc:  # a malformed run must never stop the sweep
            skipped.append({"runId": rid, "dir": c["dir"],
                            "reason": "analysis error: %s" % type(exc).__name__,
                            "detail": str(exc)[:200]})
            continue
        run["dir"] = c["dir"]
        runs.append(run)
    return flighteff.build_document(runs, skipped, dupes)


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="Wall-clock efficiency of harness auto-flights")
    ap.add_argument("--results-dir", action="append", default=[],
                    help="a harness/results dir (repeatable; default: this worktree's)")
    ap.add_argument("--results-glob", action="append", default=[],
                    help="glob of results dirs, e.g. 'C:/.../Parsek-*/harness/results'")
    ap.add_argument("--run-id", action="append", default=[], help="only this run id (repeatable)")
    ap.add_argument("--scenario", action="append", default=[],
                    help="only this scenario id (repeatable)")
    ap.add_argument("--since", default=None, help="only run ids dated >= YYYY-MM-DD (run ids carry the UTC date)")
    ap.add_argument("--per-run", action="store_true",
                    help="print every run's report (default only when one run is selected)")
    ap.add_argument("--top", type=int, default=10, help="rows per ranked table")
    ap.add_argument("--json-out", default=None, help="write the full JSON document here")
    args = ap.parse_args(argv)

    dirs: List[str] = list(args.results_dir)
    for pattern in args.results_glob:
        dirs.extend(sorted(p for p in glob.glob(pattern) if os.path.isdir(p)))
    if not dirs:
        dirs = [os.path.join(_HARNESS_DIR, "results")]
    doc = collect(dirs, args.since, args.scenario, args.run_id)
    runs = doc["runs"]
    if args.per_run or len(runs) == 1:
        for run in runs:
            print(flighteff.render_run(run, top=args.top))
            print("")
    if len(runs) != 1:
        print(flighteff.render_aggregate(doc, top=args.top))
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8", newline="\n") as f:
            f.write(flighteff.to_json(doc))
            f.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
