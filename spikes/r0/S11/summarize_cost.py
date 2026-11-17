#!/usr/bin/env python3
"""SPIKE BRANCH ONLY (spike/mp-r0, never merged). Plan: r0-spikes.md, task S11.

Summarize the ghost cost sweep's CSV output (the in-game test SpikeS11.GhostCostSweep
writes <instance>/parsek-spike/S11/{steps,fit,firstopen,settings}.csv; the owner moves
that folder per run into <umbrella>/logs/spikes/S11/<date>/<label>/).

Usage:
    python summarize_cost.py <folder> [<folder> ...]   # each holds steps.csv etc.
    python summarize_cost.py --selftest

Prints, per run label: the ten measured windows (frame mean / p99 ms, engine mean us,
built / on-screen ghosts, valid), the slope fits per view, the sidecar first-open
attempts, and a closing verdict line:

    summary: runs=<n> steps=<n> invalid=<n> firstopenOk=<n>/<n> result=<PASS|INCOMPLETE>

PASS means every run has ten windows, every window is valid, and each run's first
first-open attempt of the trajectory decoded. Stdlib only; reads, never writes.
"""

import csv
import io
import os
import sys
import tempfile

EXPECTED_COUNTS = (0, 8, 16, 32, 64)
VIEWS = ("flight", "map")


def _rows(path):
    if not os.path.isfile(path):
        return []
    with io.open(path, "r", encoding="utf-8", newline="") as fh:
        return list(csv.DictReader(fh))


def _f(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return float("nan")


def _fmt(v, nd=2):
    x = _f(v)
    return "NaN" if x != x else ("%.*f" % (nd, x))


def load(folders):
    """All rows of the three CSVs across the given folders, keyed by run label."""
    runs = {}
    for folder in folders:
        for kind in ("steps", "fit", "firstopen", "settings"):
            for row in _rows(os.path.join(folder, kind + ".csv")):
                run = row.get("run", "")
                r = runs.setdefault(run, {"steps": [], "fit": [], "firstopen": [], "settings": []})
                r[kind].append(row)
    return runs


def summarize(runs, out):
    total_steps = invalid = fo_ok = fo_runs = 0
    complete = True
    for run in sorted(runs):
        r = runs[run]
        if not run:
            continue
        settings = {s.get("key"): s.get("value") for s in r["settings"]}
        out.write("## run %s\n\n" % run)
        if settings:
            out.write("settings: screen=%s vSyncCount=%s targetFrameRate=%s quality=%s gpu=%s\n\n" % (
                settings.get("screen", "?"), settings.get("vSyncCount", "?"),
                settings.get("targetFrameRate", "?"), settings.get("qualityLevel", "?"),
                settings.get("gpu", "?")))
        out.write("| view | n | built | onScreen | meanMs | p99Ms | maxMs | fps | engineMeanUs | engineP99Us | gen0 | valid |\n")
        out.write("|---|---|---|---|---|---|---|---|---|---|---|---|\n")
        seen = set()
        for s in sorted(r["steps"], key=lambda s: (VIEWS.index(s.get("view")) if s.get("view") in VIEWS else 9,
                                                  int(_f(s.get("requested")) if _f(s.get("requested")) == _f(s.get("requested")) else 0))):
            total_steps += 1
            ok = s.get("valid") == "true"
            if not ok:
                invalid += 1
            seen.add((s.get("view"), s.get("requested")))
            out.write("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |\n" % (
                s.get("view"), s.get("requested"), s.get("built"), s.get("onScreen"),
                _fmt(s.get("meanMs"), 3), _fmt(s.get("p99Ms"), 3), _fmt(s.get("maxMs"), 3),
                _fmt(s.get("meanFps"), 1), _fmt(s.get("engineMeanUs"), 1), _fmt(s.get("engineP99Us"), 1),
                s.get("gen0"), "yes" if ok else "NO (%s)" % s.get("invalidReason", "")))
        want = {(v, str(n)) for v in VIEWS for n in EXPECTED_COUNTS}
        missing = sorted(want - seen)
        if missing:
            complete = False
            out.write("\nmissing windows: %s\n" % ", ".join("%s n=%s" % m for m in missing))
        if r["fit"]:
            out.write("\n| view | metric | points | slope per ghost | intercept | r2 |\n|---|---|---|---|---|---|\n")
            for f in r["fit"]:
                out.write("| %s | %s | %s | %s | %s | %s |\n" % (
                    f.get("view"), f.get("metric"), f.get("points"), _fmt(f.get("slopePerGhost"), 4),
                    _fmt(f.get("intercept"), 3), _fmt(f.get("r2"), 3)))
        if r["firstopen"]:
            fo_runs += 1
            out.write("\n| attempt | kind | file | bytes | probeMs | decodeMs | totalMs | items | ok |\n|---|---|---|---|---|---|---|---|---|\n")
            first_ok = False
            for f in r["firstopen"]:
                out.write("| %s | %s | %s | %s | %s | %s | %s | %s | %s |\n" % (
                    f.get("attempt"), f.get("kind"), f.get("file"), f.get("bytes"), _fmt(f.get("probeMs"), 3),
                    _fmt(f.get("decodeMs"), 3), _fmt(f.get("totalMs"), 3), f.get("items"), f.get("ok")))
                if f.get("attempt") == "1" and f.get("kind") == "trajectory" and f.get("ok") == "true":
                    first_ok = True
            if first_ok:
                fo_ok += 1
        out.write("\n")
    nruns = len([k for k in runs if k])
    result = "PASS" if (nruns > 0 and complete and invalid == 0 and fo_ok == nruns) else "INCOMPLETE"
    out.write("summary: runs=%d steps=%d invalid=%d firstopenOk=%d/%d result=%s\n" % (
        nruns, total_steps, invalid, fo_ok, fo_runs, result))
    return result


def _selftest():
    tmp = tempfile.mkdtemp(prefix="s11-selftest-")
    steps_header = ["run", "pass", "view", "requested", "built", "active", "leftover", "onScreen", "zone1", "zone2",
                    "engineFxGhosts", "engineParticleSystems", "frames", "meanMs", "p50Ms", "p95Ms", "p99Ms",
                    "minMs", "maxMs", "stdMs", "meanFps", "engineMeanUs", "engineP50Us", "engineP99Us",
                    "engineMaxUs", "gen0", "managedDeltaKb", "spawnWaitFrames", "spawnWaitTimedOut",
                    "spawnUs", "spawnMaxFrameUs", "valid", "invalidReason"]
    run = "asis-20261117T000000Z"
    with io.open(os.path.join(tmp, "steps.csv"), "w", encoding="utf-8", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(steps_header)
        for view in VIEWS:
            for n in EXPECTED_COUNTS:
                ms = 16.0 + 0.05 * n
                row = {k: "0" for k in steps_header}
                row.update(run=run, view=view, requested=str(n), built=str(n), onScreen=str(n if view == "flight" else -1),
                           meanMs=repr(ms), p99Ms=repr(ms + 2), maxMs=repr(ms + 5), meanFps=repr(1000 / ms),
                           engineMeanUs=repr(10.0 * n), engineP99Us=repr(12.0 * n), valid="true", invalidReason="")
                row["pass"] = "asis"
                w.writerow([row[k] for k in steps_header])
    with io.open(os.path.join(tmp, "firstopen.csv"), "w", encoding="utf-8", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["run", "attempt", "kind", "file", "bytes", "probeMs", "decodeMs", "totalMs", "items", "ok", "error"])
        w.writerow([run, "1", "trajectory", "x.prec", "250106", "0.4", "11.5", "12.0", "5000", "true", ""])
    buf = io.StringIO()
    assert summarize(load([tmp]), buf) == "PASS", buf.getvalue()
    assert "| flight | 64 | 64 | 64 | 19.200 |" in buf.getvalue(), buf.getvalue()

    # a broken window and a missing one make it INCOMPLETE
    with io.open(os.path.join(tmp, "steps.csv"), "r", encoding="utf-8") as fh:
        lines = fh.read().splitlines()
    lines = [l for l in lines if ",map,64," not in l]
    lines = [l.replace(",true,", ",false,built 7 of 8") if ",flight,8," in l else l for l in lines]
    with io.open(os.path.join(tmp, "steps.csv"), "w", encoding="utf-8", newline="") as fh:
        fh.write("\n".join(lines) + "\n")
    buf = io.StringIO()
    assert summarize(load([tmp]), buf) == "INCOMPLETE", buf.getvalue()
    text = buf.getvalue()
    assert "missing windows: map n=64" in text, text
    assert "invalid=1" in text, text
    assert summarize(load([os.path.join(tmp, "nope")]), io.StringIO()) == "INCOMPLETE"
    for name in os.listdir(tmp):
        os.remove(os.path.join(tmp, name))
    os.rmdir(tmp)
    print("summarize_cost selftest: PASS")
    return 0


def main(argv):
    if argv[1:] == ["--selftest"]:
        return _selftest()
    if len(argv) < 2:
        sys.stderr.write(__doc__)
        return 2
    result = summarize(load(argv[1:]), sys.stdout)
    return 0 if result == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
