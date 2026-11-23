"""Write and check the part ghost parity baseline (design-part-ghost-parity.md section 10).

    python harness/tools/part_parity_baseline.py write --label pp1 DIR [DIR ...]
    python harness/tools/part_parity_baseline.py check --label pp1 DIR
    python harness/tools/part_parity_baseline.py spread DIR_A DIR_B
    python harness/tools/part_parity_baseline.py accepted [--profile P] DIR [DIR ...]

Each DIR is one sweep directory holding ``manifest.json`` and the ``<caseId>.part.json``
sidecars: a harvested ``harness/results/<runId>_partparity/<label>/`` or the archived
``<umbrella>/automation/results-archive/<runId>/partparity/<label>/`` (the archive keeps
the sidecars and the manifest, which is all this tool reads; atlases are never needed).

``write`` pins the label's section of ``harness/partparity/baseline-<profile>.json`` from
the reading runs given (the measure bands over them, ``partparity.build_baseline_section``),
keeping every other label's section. It refuses, exit 3, while any run has a failing case
whose fail reason ``harness/partparity/accepted.toml`` does not cover, and prints a TOML
skeleton for each so the reason and the todo id can be written by hand: a difference is
never accepted silently. ``check`` runs the armed gate (``partparity.gate_sweep``) of one
sweep against the committed files, exit 1 on a red. ``spread`` measures the run-to-run
movement of every baselined measure between two sweeps of one label (the basis of the
drift tolerances). ``accepted`` lists the unaccepted fail reasons of the sweeps given.

Stdlib only. Writes only the baseline file it was asked to write.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
import tomllib
from typing import Dict, List, Optional, Sequence, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_LIB = os.path.join(os.path.dirname(_HERE), "lib")
if _LIB not in sys.path:
    sys.path.insert(0, _LIB)

import partparity as pp  # noqa: E402

DEFAULT_BASELINE_DIR = os.path.join(os.path.dirname(_HERE), pp.BASELINE_DIR_NAME)
EXIT_UNACCEPTED = 3
EXIT_RED = 1
EXIT_USAGE = 2


# ---------------------------------------------------------------------------
# Pure helpers.
# ---------------------------------------------------------------------------

def render_baseline(doc: Dict) -> str:
    """The baseline file text: the header keys indented, each case on ONE line (sorted
    keys), so a re-pin's diff names the cases that moved."""
    lines = ["{"]
    lines.append('  "schema": %s,' % json.dumps(doc["schema"]))
    lines.append('  "profile": %s,' % json.dumps(doc["profile"]))
    lines.append('  "labels": {')
    labels = sorted(doc["labels"])
    for i, label in enumerate(labels):
        section = doc["labels"][label]
        lines.append("    %s: {" % json.dumps(label))
        for key in ("runs", "dllHashes", "writtenUtc", "tolerances"):
            lines.append("      %s: %s," % (json.dumps(key),
                                            json.dumps(section.get(key), sort_keys=True)))
        lines.append('      "cases": {')
        cids = sorted(section.get("cases") or {})
        for j, cid in enumerate(cids):
            lines.append("        %s: %s%s" % (json.dumps(cid),
                                               json.dumps(section["cases"][cid], sort_keys=True),
                                               "," if j < len(cids) - 1 else ""))
        lines.append("      }")
        lines.append("    }%s" % ("," if i < len(labels) - 1 else ""))
    lines.append("  }")
    lines.append("}")
    return "\n".join(lines) + "\n"


def merge_section(doc: Optional[Dict], profile: str, label: str, section: Dict) -> Dict:
    """The baseline document with ``label``'s section replaced (other labels kept)."""
    out = {"schema": pp.BASELINE_SCHEMA, "profile": profile, "labels": {}}
    if isinstance(doc, dict) and isinstance(doc.get("labels"), dict):
        out["labels"].update(doc["labels"])
    out["labels"][label] = section
    return out


def accepted_skeleton(case_id: str, profile: Optional[str], reason: "pp.FailReason") -> str:
    """A TOML block for one unaccepted fail reason. ``reason`` and ``todo`` are left
    empty on purpose: ``partparity.parse_accepted`` refuses them until written."""
    view = reason.view or pp.VIEW_ANY
    lines = ["[[accepted]]", 'case = "%s"' % case_id]
    if profile:
        lines.append('profile = "%s"' % profile)
    lines += ['view = "%s"' % view, 'measure = "%s"' % reason.group,
              '# fail reason: %s' % reason.reason, 'reason = ""', 'todo = ""']
    return "\n".join(lines)


def spread_table(a: Dict[str, Optional[Dict]], b: Dict[str, Optional[Dict]],
                 top: int = 10, skip: Sequence["pp.AcceptedEntry"] = (),
                 profile: Optional[str] = None) -> List[str]:
    """Per measure, the run-to-run movement |x - y| over every case both sweeps measured
    (setup faults left out): count, nonzero, p50 / p90 / p99 / max, then the ``top``
    largest moves with the tolerance each would need (the absolute part over the larger
    value's relative allowance)."""
    rows: Dict[str, List[Tuple[float, str, str, float, float]]] = {m: [] for m in pp.BASELINE_MEASURES}
    for cid in sorted(set(a) & set(b)):
        sa, sb = a.get(cid), b.get(cid)
        if not isinstance(sa, dict) or not isinstance(sb, dict) or cid in pp.ALL_CONTROL_IDS:
            continue
        if pp.is_setup_fault((sa.get("setup") or {}).get("status")) or \
                pp.is_setup_fault((sb.get("setup") or {}).get("status")):
            continue
        ma, mb = pp.case_measures(sa), pp.case_measures(sb)
        for vk in sorted(set(ma) & set(mb)):
            for m in pp.BASELINE_MEASURES:
                if m in ma[vk] and m in mb[vk]:
                    if any(e.noisy and e.covers(cid, profile, vk, pp.MEASURE_GROUP[m]) for e in skip):
                        continue
                    x, y = ma[vk][m], mb[vk][m]
                    rows[m].append((abs(x - y), cid, vk, x, y))
    out = []
    for m in pp.BASELINE_MEASURES:
        r = sorted(rows[m], key=lambda t: (-t[0], t[1], t[2]))
        if not r:
            out.append("%s: no pairs" % m)
            continue
        n = len(r)

        def q(p: float) -> float:
            return r[min(n - 1, int(round((1.0 - p) * (n - 1))))][0]
        out.append("%s: n=%d nonzero=%d p50=%g p90=%g p99=%g max=%g"
                   % (m, n, sum(1 for t in r if t[0] > 0), q(0.5), q(0.9), q(0.99), r[0][0]))
        for d, cid, vk, x, y in r[:top]:
            if d == 0:
                break
            # Pinned on the better run alone, would the worse one drift?
            best, worst = (max(x, y), min(x, y)) if m == "iou" else (min(x, y), max(x, y))
            verdict = "inside" if pp.measure_drift(m, worst, [best, best]) == 0 else "DRIFT"
            out.append("    d=%-10s %s %s %s -> %s (pinned on the better run: %s)"
                       % (("%.4f" % d) if m == "iou" else "%d" % d, cid, vk,
                          pp._fmt(m, x), pp._fmt(m, y), verdict))
    return out


# ---------------------------------------------------------------------------
# I/O shell.
# ---------------------------------------------------------------------------

def read_sweep_dir(directory: str) -> Tuple[Dict, Dict[str, Optional[Dict]]]:
    """(manifest, sidecars by case id) of one sweep directory."""
    with open(os.path.join(directory, pp.MANIFEST_FILE), "r", encoding="utf-8-sig") as fh:
        manifest = json.load(fh)
    sidecars: Dict[str, Optional[Dict]] = {}
    for name in sorted(os.listdir(directory)):
        if not name.endswith(pp.SIDECAR_SUFFIX):
            continue
        try:
            with open(os.path.join(directory, name), "r", encoding="utf-8-sig") as fh:
                doc = json.load(fh)
            sidecars[name[:-len(pp.SIDECAR_SUFFIX)]] = doc if isinstance(doc, dict) else None
        except (OSError, ValueError):
            sidecars[name[:-len(pp.SIDECAR_SUFFIX)]] = None
    return manifest, sidecars


def run_id_of(directory: str) -> str:
    """The run id a sweep directory belongs to: ``<runId>_partparity/<label>`` or
    ``<runId>/partparity/<label>``, else the directory's own path."""
    parent = os.path.dirname(os.path.normpath(directory))
    leaf = os.path.basename(parent)
    if leaf.endswith("_partparity"):
        return leaf[:-len("_partparity")]
    if leaf == "partparity":
        return os.path.basename(os.path.dirname(parent))
    return os.path.normpath(directory)


def load_accepted(base_dir: str) -> List["pp.AcceptedEntry"]:
    path = os.path.join(base_dir, pp.ACCEPTED_FILE)
    if not os.path.isfile(path):
        return []
    with open(path, "rb") as fh:
        doc = tomllib.load(fh)
    entries, errors = pp.parse_accepted(doc)
    if errors:
        raise ValueError("%s: %s" % (path, "; ".join(errors)))
    return entries


def load_baseline(base_dir: str, profile: str) -> Optional[Dict]:
    path = os.path.join(base_dir, pp.baseline_file_name(profile))
    if not os.path.isfile(path):
        return None
    with open(path, "r", encoding="utf-8") as fh:
        doc = json.load(fh)
    errors = pp.validate_baseline(doc, profile)
    if errors:
        raise ValueError("%s: %s" % (path, "; ".join(errors[:5])))
    return doc


def _judge(sidecars: Dict[str, Optional[Dict]], manifest: Dict) -> List["pp.CaseVerdict"]:
    ids = [e["caseId"] for e in manifest.get("cases") or []
           if isinstance(e, dict) and isinstance(e.get("caseId"), str)
           and e["caseId"] not in pp.ALL_CONTROL_IDS]
    return [pp.judge_case(sidecars[c]) for c in ids if isinstance(sidecars.get(c), dict)]


def _profile(manifest: Dict, explicit: Optional[str]) -> Optional[str]:
    return explicit or pp.profile_of(manifest, None)


def cmd_write(args) -> int:
    runs = []
    profile = None
    accepted = load_accepted(args.baseline_dir)
    unaccepted: List[str] = []
    for d in args.dirs:
        manifest, sidecars = read_sweep_dir(d)
        if manifest.get("label") != args.label:
            print("part_parity_baseline: %s is label %s, not %s" % (d, manifest.get("label"), args.label),
                  file=sys.stderr)
            return EXIT_USAGE
        p = _profile(manifest, args.profile)
        if profile is not None and p != profile:
            print("part_parity_baseline: %s is profile %s, the others %s" % (d, p, profile),
                  file=sys.stderr)
            return EXIT_USAGE
        profile = p
        if not (manifest.get("progress") or {}).get("complete"):
            print("part_parity_baseline: %s is not a complete sweep" % d, file=sys.stderr)
            return EXIT_USAGE
        verdicts = _judge(sidecars, manifest)
        for cid, fr in pp.unaccepted_fails(verdicts, profile, accepted):
            unaccepted.append(accepted_skeleton(cid, profile, fr))
        runs.append((run_id_of(d), manifest, sidecars))
    if profile is None:
        print("part_parity_baseline: no profile (pass --profile)", file=sys.stderr)
        return EXIT_USAGE
    if unaccepted:
        seen = []
        for block in unaccepted:
            if block not in seen:
                seen.append(block)
        print("part_parity_baseline: %d unaccepted fail reason(s); write a reason and a todo id "
              "for each into %s, or fix the ghost, then re-run:\n"
              % (len(seen), os.path.join(args.baseline_dir, pp.ACCEPTED_FILE)), file=sys.stderr)
        print("\n\n".join(seen))
        return EXIT_UNACCEPTED
    hashes = {m.get("dllHash") for _r, m, _s in runs}
    if len(hashes) > 1:
        print("part_parity_baseline: note: the runs carry %d different DLL hashes" % len(hashes))
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    section = pp.build_baseline_section(runs, stamp)
    doc = merge_section(load_baseline(args.baseline_dir, profile), profile, args.label, section)
    os.makedirs(args.baseline_dir, exist_ok=True)
    path = os.path.join(args.baseline_dir, pp.baseline_file_name(profile))
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(render_baseline(doc))
    os.replace(tmp, path)
    banded = sum(1 for e in section["cases"].values() if e["views"])
    print("part_parity_baseline: wrote %s label=%s runs=%s cases=%d banded=%d"
          % (path, args.label, ",".join(section["runs"]), len(section["cases"]), banded))
    return 0


def cmd_check(args) -> int:
    manifest, sidecars = read_sweep_dir(args.dir)
    label = args.label or manifest.get("label")
    profile = _profile(manifest, args.profile)
    doc = load_baseline(args.baseline_dir, profile) if profile else None
    section = (doc or {}).get("labels", {}).get(label) if doc else None
    gate = pp.gate_sweep(label, profile, manifest, sidecars, _judge(sidecars, manifest),
                         section, load_accepted(args.baseline_dir))
    counts = gate.counts()
    print("part_parity_baseline: check label=%s profile=%s gate=%s %s"
          % (label, profile, "ok" if gate.ok else "red",
             " ".join("%s=%d" % (k, v) for k, v in sorted(counts.items()) if v)))
    for f in gate.reds:
        print("  RED  " + f.text())
    if args.notes:
        for f in gate.notes:
            print("  note " + f.text())
    return 0 if gate.ok else EXIT_RED


def cmd_spread(args) -> int:
    ma, sa = read_sweep_dir(args.a)
    mb, sb = read_sweep_dir(args.b)
    print("part_parity_baseline: spread %s (%s) vs %s (%s)"
          % (run_id_of(args.a), (ma.get("dllHash") or "")[:8], run_id_of(args.b),
             (mb.get("dllHash") or "")[:8]))
    skip = load_accepted(args.baseline_dir) if args.skip_noisy else []
    for line in spread_table(sa, sb, args.top, skip, _profile(ma, args.profile)):
        print("  " + line)
    return 0


def cmd_accepted(args) -> int:
    accepted = load_accepted(args.baseline_dir)
    blocks = []
    for d in args.dirs:
        manifest, sidecars = read_sweep_dir(d)
        profile = _profile(manifest, args.profile)
        for cid, fr in pp.unaccepted_fails(_judge(sidecars, manifest), profile, accepted):
            block = accepted_skeleton(cid, profile, fr)
            if block not in blocks:
                blocks.append(block)
    print("\n\n".join(blocks) if blocks else "# every fail reason is accepted")
    return EXIT_UNACCEPTED if blocks else 0


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--baseline-dir", default=DEFAULT_BASELINE_DIR,
                    help="where baseline-<profile>.json and accepted.toml live "
                         "(default harness/partparity)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    w = sub.add_parser("write", help="pin a label's baseline section from reading runs")
    w.add_argument("--label", required=True)
    w.add_argument("--profile")
    w.add_argument("dirs", nargs="+")
    c = sub.add_parser("check", help="run the armed gate of one sweep against the committed files")
    c.add_argument("--label")
    c.add_argument("--profile")
    c.add_argument("--notes", action="store_true", help="also print the report-only notes")
    c.add_argument("dir")
    s = sub.add_parser("spread", help="run-to-run movement of every measure between two sweeps")
    s.add_argument("--top", type=int, default=10)
    s.add_argument("--profile")
    s.add_argument("--skip-noisy", action="store_true",
                   help="leave out the measures a noisy accepted entry covers")
    s.add_argument("a")
    s.add_argument("b")
    a = sub.add_parser("accepted", help="TOML skeletons for every unaccepted fail reason")
    a.add_argument("--profile")
    a.add_argument("dirs", nargs="+")
    args = ap.parse_args(argv)
    return {"write": cmd_write, "check": cmd_check, "spread": cmd_spread,
            "accepted": cmd_accepted}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
