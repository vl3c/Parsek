#!/usr/bin/env python3
"""Report-only mutation checker over archived harness runs (trust risk 8, phases 1-2).

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
    python tools/mutation_check.py --save-only           # save-level edits only (phase 2)
    python tools/mutation_check.py --ledger-only         # ledger-oracle edits only (phase 2)

``--save-only`` runs only the save-structure checks (``lib/mutsave.py``): for every
spec with an ARMED save-parse block, the newest archived produced save whose armed
blocks pass, else the spec's committed ``fixture.saveTemplate`` when that passes;
no KSP.log is needed, so it also reaches lanes whose logs no longer replay green.

``--ledger-only`` runs only the ledger-oracle checks (``lib/mutledger.py``): for every
spec with an ``[expectations.ledger]`` or ``[expectations.world]`` block, the newest
results archive whose verifier passes over its archived seed (``<runId>.manifest.json``),
produced careerSave (the snapshot's ``analysis/*.analysis.json``) and KSP.log.
Collect-logs folders carry no seed, so they never serve.

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

import mutledger  # noqa: E402
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
        manifest = os.path.join(results_dir, "%s.manifest.json" % run_id)
        out.append(mutlib.ArchiveRef(parsed[0], str(data["scenarioId"]), "results", run_id,
                                     log_path, save_dir, rec_dir, data.get("verdict"),
                                     bool(art.get("kspLogTruncated")),
                                     manifest if os.path.isfile(manifest) else None))
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
    save_text = read_save_text(ref)
    snapshot = saveparse.parse_parsek_scenario(save_text) if save_text is not None else None
    label = "%s:%s%s" % (ref.source, ref.run_id, " (log truncated)" if ref.truncated else "")
    return mutlib.ArchiveInputs(label, text, count, snapshot, save_text,
                                read_ledger_inputs(ref, text))


def read_analysis_text(ref: mutlib.ArchiveRef) -> Optional[str]:
    """The produced save's single ``analysis/*.analysis.json`` (None when absent or
    ambiguous)."""
    if not ref.save_dir:
        return None
    adir = os.path.join(ref.save_dir, "analysis")
    if not os.path.isdir(adir):
        return None
    names = [n for n in os.listdir(adir) if n.endswith(".analysis.json")]
    if len(names) != 1:
        return None
    with open(os.path.join(adir, names[0]), "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def read_ledger_inputs(ref: mutlib.ArchiveRef, log_text: str) -> Optional[mutledger.LedgerInputs]:
    """Seed (the archived manifest's audit copy) + produced careerSave + log, or None
    when the archive carries no analysis file."""
    analysis = read_analysis_text(ref)
    if analysis is None:
        return None
    seed = None
    if ref.manifest_path:
        data = _read_json(ref.manifest_path)
        if isinstance(data, dict) and isinstance(data.get("seed"), dict):
            seed = data["seed"]
    return mutledger.LedgerInputs(seed, analysis, log_text)


def run_ledger_only(specs: Dict[str, Dict], wanted: List[str], refs: List[mutlib.ArchiveRef],
                    max_tries: int):
    lanes: List[mutlib.LaneReport] = []
    no_archive: List[str] = []
    for sid in wanted:
        spec = specs[sid]
        if not mutledger.is_ledger_spec(spec.get("expectations", {}) or {}):
            continue
        cands = [r for r in mutlib.order_candidates(refs, sid)
                 if r.manifest_path and r.save_dir][:max(1, max_tries)]
        lane: Optional[mutlib.LaneReport] = None
        first_red: Optional[mutlib.LaneReport] = None
        for ref in cands:
            label = "%s:%s" % (ref.source, ref.run_id)
            try:
                with open(ref.log_path, "r", encoding="utf-8", errors="replace") as fh:
                    log_text = fh.read()
                inputs = read_ledger_inputs(ref, log_text)
            except OSError as exc:
                print("skip %s: %s" % (label, exc), file=sys.stderr)
                continue
            if inputs is None:
                continue
            lane = mutlib.check_ledger_lane(spec, label, inputs)
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
    return lanes, no_archive


def read_save_text(ref: mutlib.ArchiveRef) -> Optional[str]:
    if not ref.save_dir:
        return None
    sfs = os.path.join(ref.save_dir, "persistent.sfs")
    if not os.path.isfile(sfs):
        return None
    with open(sfs, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def fixture_save_text(spec: Dict) -> Optional[str]:
    """The spec's committed fixture template (never an operator-local one)."""
    template = (spec.get("fixture") or {}).get("saveTemplate")
    if not template or mutlib.hlib.is_local_fixture_template(template):
        return None
    sfs = os.path.join(HARNESS_ROOT, template, "persistent.sfs")
    if not os.path.isfile(sfs):
        return None
    with open(sfs, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def run_save_only(specs: Dict[str, Dict], wanted: List[str], refs: List[mutlib.ArchiveRef],
                  max_tries: int, use_fixtures: bool):
    lanes: List[mutlib.LaneReport] = []
    no_archive: List[str] = []
    for sid in wanted:
        spec = specs[sid]
        if not saveparse.armed_structure_blocks(spec.get("expectations", {}) or {}):
            continue
        cands = [r for r in mutlib.order_candidates(refs, sid) if r.save_dir][:max(1, max_tries)]
        sources = [("%s:%s" % (r.source, r.run_id), r) for r in cands]
        if use_fixtures:
            sources.append(("fixture:%s" % (spec.get("fixture") or {}).get("saveTemplate"), None))
        lane: Optional[mutlib.LaneReport] = None
        first_red: Optional[mutlib.LaneReport] = None
        for label, ref in sources:
            try:
                text = read_save_text(ref) if ref is not None else fixture_save_text(spec)
            except OSError as exc:
                print("skip %s: %s" % (label, exc), file=sys.stderr)
                continue
            if text is None:
                continue
            lane = mutlib.check_save_lane(spec, label, text)
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
    return lanes, no_archive


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
    ap.add_argument("--save-only", action="store_true",
                    help="only the save-level edits over archived saves / committed fixtures")
    ap.add_argument("--no-fixtures", action="store_true",
                    help="with --save-only: do not fall back to the committed fixture template")
    ap.add_argument("--ledger-only", action="store_true",
                    help="only the ledger-oracle edits over archived seed + careerSave + log")
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

    started = time.time()
    if args.save_only:
        lanes, no_archive = run_save_only(specs, wanted, refs, args.max_tries,
                                          not args.no_fixtures)
        return finish(args, roots, started, lanes, no_archive)
    if args.ledger_only:
        lanes, no_archive = run_ledger_only(specs, wanted, refs, args.max_tries)
        return finish(args, roots, started, lanes, no_archive)
    lanes: List[mutlib.LaneReport] = []
    no_archive: List[str] = []
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
    return finish(args, roots, started, lanes, no_archive)


def finish(args, roots: List[str], started: float, lanes: List[mutlib.LaneReport],
           no_archive: List[str]) -> int:
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
                        "patterns": [p.__dict__ for p in l.patterns],
                        "saveGates": [g.__dict__ for g in l.save_gates],
                        "ledgerGates": [g.__dict__ for g in l.ledger_gates]} for l in lanes],
                      fh, indent=1)
    t = mutlib.sweep_totals(lanes, len(no_archive))
    print("sweep: lanes green=%d not-green=%d no-archive=%d mutations=%d killed=%d "
          "survived=%d triage=%d saveGates proven=%d vacuous=%d unchecked=%d "
          "ledgerGates proven=%d vacuous=%d unchecked=%d -> %s" % (
              t.lanes_green, t.lanes_not_green, t.lanes_no_archive, t.mutations, t.killed,
              t.survived, t.triage, t.gates_proven, t.gates_vacuous, t.gates_unchecked,
              t.ledger_proven, t.ledger_vacuous, t.ledger_unchecked, out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
