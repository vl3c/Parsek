#!/usr/bin/env python3
"""Report-only mutation checker over archived harness runs (trust risk 8, phase 1).

Thin I/O shell: finds archived runs on this machine, reads each one's KSP.log,
recording count and produced save, and hands them to ``lib/mutlib.py``, which
replays the harness's pure gating evaluators unmutated (the baseline must be
green) and then over mutated copies. Survivors are LISTED; nothing here fails a
run, and it never launches KSP.

Archives it reads (``--archive`` accepts any of these; the default is all of
them under the umbrella root):

- a results dir (``<worktree>/harness/results``): ``<runId>.json`` for the spec
  id and verdict, ``<runId>_shots/KSP.log`` (run.py's bounded copy),
  ``<runId>_save/`` (the produced-save snapshot);
- a collect-logs root (``<umbrella>/logs``) or one of its folders
  ``<stamp>_<specId>/``: ``KSP.log``, ``saves/<save>/persistent.sfs``,
  ``parsek/Recordings/*.prec``.

    python tools/mutation_check.py                       # every gating spec, newest green archive
    python tools/mutation_check.py --spec B1-pad-hop     # one lane
    python tools/mutation_check.py --archive ../logs/2026-09-24_0041_SD-1-same-tree-redock
    python tools/mutation_check.py --list-archives --spec B1-pad-hop

Writes ``results/mutation-check/<stamp>.md`` (gitignored) unless ``--out``.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import tomllib
from typing import Dict, List, Optional

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
LIB_DIR = os.path.join(HARNESS_ROOT, "lib")
if LIB_DIR not in sys.path:
    sys.path.insert(0, LIB_DIR)

import mutlib  # noqa: E402
import saveparse  # noqa: E402

SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")
DEFAULT_OUT_DIR = os.path.join(HARNESS_ROOT, "results", "mutation-check")
REPO_ROOT = os.path.dirname(HARNESS_ROOT)


def load_specs() -> Dict[str, Dict]:
    specs: Dict[str, Dict] = {}
    for name in sorted(os.listdir(SCENARIOS_DIR)):
        if not name.endswith(".toml"):
            continue
        with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
            spec = tomllib.load(fh)
        if spec.get("id"):
            specs[str(spec["id"])] = spec
    return specs


def _read_json(path: str) -> Optional[Dict]:
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


def _results_refs(results_dir: str) -> List[mutlib.ArchiveRef]:
    out: List[mutlib.ArchiveRef] = []
    try:
        names = os.listdir(results_dir)
    except OSError:
        return out
    for name in names:
        if not name.endswith(".json"):
            continue
        parsed = mutlib.parse_stamped_name(name[:-5])
        if parsed is None:
            continue
        data = _read_json(os.path.join(results_dir, name))
        if not data or not data.get("scenarioId"):
            continue
        run_id = str(data.get("runId") or name[:-5])
        art = data.get("artifacts") or {}
        shots = art.get("shotsDir") or ("%s_shots" % run_id)
        log_path = os.path.join(results_dir, shots, "KSP.log")
        if not os.path.isfile(log_path):
            continue
        snap = (data.get("snapshot") or {}).get("path") or ("%s_save" % run_id)
        save_dir = os.path.join(results_dir, snap)
        if not os.path.isdir(save_dir):
            save_dir = None
        rec_dir = os.path.join(save_dir, "Parsek", "Recordings") if save_dir else None
        out.append(mutlib.ArchiveRef(parsed[0], str(data["scenarioId"]), "results", run_id,
                                     log_path, save_dir, rec_dir, data.get("verdict"),
                                     bool(art.get("kspLogTruncated"))))
    return out


def _collect_ref(folder: str) -> Optional[mutlib.ArchiveRef]:
    name = os.path.basename(os.path.normpath(folder))
    parsed = mutlib.parse_stamped_name(name)
    log_path = os.path.join(folder, "KSP.log")
    if parsed is None or not os.path.isfile(log_path):
        return None
    save_dir = None
    saves = os.path.join(folder, "saves")
    if os.path.isdir(saves):
        subs = [d for d in sorted(os.listdir(saves))
                if os.path.isfile(os.path.join(saves, d, "persistent.sfs"))]
        if len(subs) == 1:
            save_dir = os.path.join(saves, subs[0])
    rec_dir = os.path.join(folder, "parsek", "Recordings")
    if not os.path.isdir(rec_dir):
        rec_dir = os.path.join(save_dir, "Parsek", "Recordings") if save_dir else None
    return mutlib.ArchiveRef(parsed[0], parsed[1], "collect", name, log_path, save_dir,
                             rec_dir, None, False)


def discover(paths: List[str]) -> List[mutlib.ArchiveRef]:
    refs: List[mutlib.ArchiveRef] = []
    for p in paths:
        if not os.path.isdir(p):
            continue
        if os.path.isfile(os.path.join(p, "KSP.log")):
            name = os.path.basename(os.path.normpath(p))
            if name.endswith("_shots"):
                parent = os.path.dirname(os.path.normpath(p))
                refs.extend(r for r in _results_refs(parent)
                            if r.run_id == name[:-len("_shots")])
            else:
                ref = _collect_ref(p)
                if ref is not None:
                    refs.append(ref)
            continue
        if any(n.endswith(".json") for n in os.listdir(p)):
            refs.extend(_results_refs(p))
        for n in os.listdir(p):
            sub = os.path.join(p, n)
            if os.path.isfile(os.path.join(sub, "KSP.log")) and not n.endswith("_shots"):
                ref = _collect_ref(sub)
                if ref is not None:
                    refs.append(ref)
    return refs


def default_archive_roots(umbrella: str) -> List[str]:
    roots: List[str] = []
    for n in sorted(os.listdir(umbrella)):
        res = os.path.join(umbrella, n, "harness", "results")
        if os.path.isdir(res):
            roots.append(res)
    logs = os.path.join(umbrella, "logs")
    if os.path.isdir(logs):
        roots.append(logs)
    return roots


def read_inputs(ref: mutlib.ArchiveRef) -> mutlib.ArchiveInputs:
    with open(ref.log_path, "r", encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    count: Optional[int] = None
    if ref.recordings_dir and os.path.isdir(ref.recordings_dir):
        count = sum(1 for f in os.listdir(ref.recordings_dir) if f.endswith(".prec"))
    snapshot = None
    if ref.save_dir:
        sfs = os.path.join(ref.save_dir, "persistent.sfs")
        if os.path.isfile(sfs):
            with open(sfs, "r", encoding="utf-8", errors="replace") as fh:
                snapshot = saveparse.parse_parsek_scenario(fh.read())
    label = "%s:%s%s" % (ref.source, ref.run_id, " (log truncated)" if ref.truncated else "")
    return mutlib.ArchiveInputs(label, text, count, snapshot)


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--spec", action="append", default=[], help="spec id (repeatable); default every gating spec")
    ap.add_argument("--archive", action="append", default=[],
                    help="results dir, collect-logs root, or one run folder (repeatable)")
    ap.add_argument("--umbrella-root", default=os.path.dirname(REPO_ROOT))
    ap.add_argument("--max-tries", type=int, default=3,
                    help="newest archives tried per spec until one baseline is green")
    ap.add_argument("--out", help="report path (default results/mutation-check/<stamp>.md)")
    ap.add_argument("--json", help="also write the per-lane records as JSON")
    ap.add_argument("--include-info", action="store_true", help="list free-field survivors too")
    ap.add_argument("--list-archives", action="store_true")
    args = ap.parse_args(argv)

    specs = load_specs()
    if args.spec:
        missing = [s for s in args.spec if s not in specs]
        if missing:
            print("unknown spec id(s): %s" % ", ".join(missing), file=sys.stderr)
            return 2
        wanted = list(args.spec)
    else:
        wanted = [sid for sid, sp in specs.items()
                  if mutlib.spec_gating_surfaces(sp) and not mutlib.is_expected_fail_lane(sp)]

    roots = args.archive or default_archive_roots(args.umbrella_root)
    refs = discover(roots)
    if args.list_archives:
        for sid in wanted:
            for r in mutlib.order_candidates(refs, sid):
                print("%s  %s  %s  verdict=%s  %s" % (sid, r.source, r.run_id, r.verdict, r.log_path))
        return 0

    lanes: List[mutlib.LaneReport] = []
    no_archive: List[str] = []
    started = time.time()
    for sid in wanted:
        cands = mutlib.order_candidates(refs, sid)[:max(1, args.max_tries)]
        if not cands:
            no_archive.append(sid)
            continue
        lane: Optional[mutlib.LaneReport] = None
        first_red: Optional[mutlib.LaneReport] = None
        for ref in cands:
            try:
                inputs = read_inputs(ref)
            except OSError as exc:
                print("skip %s: %s" % (ref.log_path, exc), file=sys.stderr)
                continue
            lane = mutlib.check_lane(specs[sid], inputs)
            if lane.baseline == mutlib.BASELINE_GREEN:
                break
            first_red = first_red or lane
            lane = None
        lane = lane or first_red
        if lane is None:
            no_archive.append(sid)
            continue
        lanes.append(lane)
        print(mutlib.summary_line(lane), flush=True)

    stamp = time.strftime("%Y-%m-%d_%H%M%S")
    header = "Generated %s over %d archive root(s) in %.0f s; specs from `%s`." % (
        stamp, len(roots), time.time() - started, os.path.relpath(SCENARIOS_DIR, REPO_ROOT).replace(os.sep, "/"))
    report = mutlib.render_report(lanes, no_archive, header, args.include_info)
    out = args.out or os.path.join(DEFAULT_OUT_DIR, "%s.md" % stamp)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        fh.write(report)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump([{"spec": l.spec_id, "archive": l.archive, "baseline": l.baseline,
                        "baselineReasons": l.baseline_reasons, "notes": l.notes,
                        "mutations": [m.__dict__ for m in l.mutations],
                        "patterns": [p.__dict__ for p in l.patterns]} for l in lanes],
                      fh, indent=1)
    t = mutlib.sweep_totals(lanes, len(no_archive))
    print("sweep: lanes green=%d not-green=%d no-archive=%d mutations=%d killed=%d "
          "survived=%d triage=%d -> %s" % (t.lanes_green, t.lanes_not_green,
                                           t.lanes_no_archive, t.mutations, t.killed,
                                           t.survived, t.triage, out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
