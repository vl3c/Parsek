#!/usr/bin/env python3
"""The harness run index: flight results as data, plus runtime health per lane.

Thin I/O shell over ``harness/lib/runindex.py`` (every decision, the row schema
and the health facts are documented there). It reads the machine-wide results
archive ``<umbrella>/automation/results-archive/<runId>/`` that run.py fills,
writes one JSON line per archived run attempt to ``index.jsonl``, answers
questions from it, and moves it to and from a git branch so a session without
the archive can read it.

    python harness/tools/run_index.py rebuild [--archive DIR] [--out FILE] [--health-out FILE] [--jobs N]
    python harness/tools/run_index.py append --run RUNID [--archive DIR] [--index FILE]
    python harness/tools/run_index.py lane LANE [--index FILE] [--now ISO] [--json]
    python harness/tools/run_index.py summary [--index FILE] [--json]
    python harness/tools/run_index.py lane-table [--index FILE]
    python harness/tools/run_index.py trend LANE [--last N] [--index FILE]
    python harness/tools/run_index.py health-regressions [--index FILE]
    python harness/tools/run_index.py health-build [--index FILE] [--archive DIR] [--out FILE]
    python harness/tools/run_index.py lanes-for (--module M | --subsystem S) [--health FILE]
    python harness/tools/run_index.py publish --index FILE [--health FILE] --repo DIR --remote R --branch B
    python harness/tools/run_index.py fetch --repo DIR --remote R --branch B --out FILE [--health-out FILE]

Defaults: ``--archive`` is ``<umbrella>/automation/results-archive`` (the
umbrella is this worktree's parent), ``--index`` / ``--out`` is
``<archive>/index.jsonl``, ``--health`` is ``<archive>/lane-health.json``.
``publish`` / ``fetch`` take ``--branch`` with no default: which branch may hold
the index is the owner's decision.

Nothing here changes a run's verdict. run.py calls ``append_runs`` after it
archives a scenario's attempts and logs one line whatever happens.
"""

from __future__ import annotations

import argparse
import gzip
import json
import os
import subprocess
import sys
import time
import zlib
from typing import Callable, Dict, List, Optional, Sequence, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_DIR = os.path.dirname(_HERE)
_LIB_DIR = os.path.join(_HARNESS_DIR, "lib")
if _LIB_DIR not in sys.path:
    sys.path.insert(0, _LIB_DIR)

import runindex  # noqa: E402

WORKTREE_ROOT = os.path.dirname(_HARNESS_DIR)
DEFAULT_ARCHIVE_DIR = os.path.join(os.path.dirname(WORKTREE_ROOT), "automation", "results-archive")
DEFAULT_SCENARIOS_DIR = os.path.join(_HARNESS_DIR, "scenarios")
DEFAULT_TAG_MAP = os.path.join(WORKTREE_ROOT, "scripts", "arch", "log_tags.json")
KSP_LOG_GZ = "KSP.log.gz"
MANIFEST_NAME = "manifest.json"
BRANCH_README = """# Parsek harness run index

This branch holds the harness run index, written by `harness/tools/run_index.py publish`
from the machine-wide results archive (`<umbrella>/automation/results-archive/`):

- `index.jsonl`: one JSON line per archived run attempt (schema `parsek-run-index/1`).
- `lane-health.json`: per lane, from its latest PASS, Parsek log lines per subsystem
  and per module (schema `parsek-lane-health/1`).

`harness/lib/runindex.py` documents every key. Read it with
`python harness/tools/run_index.py fetch --repo . --remote origin --branch <this branch> --out index.jsonl`
and then `run_index.py lane <LANE> --index index.jsonl`. Publishing merges by run id
(and by lane), so two machines may both publish. Publishing writes only this file,
`index.jsonl` and `lane-health.json`; every other file on the branch is kept. Do not
edit these three by hand.
"""
# The tool's own files on the branch: it writes these and nothing else. Its
# readme has its own name so a README.md the branch already holds is never
# touched.
BRANCH_README_NAME = "RUN-INDEX.md"
OWN_FILES = (BRANCH_README_NAME, runindex.INDEX_FILE_NAME, runindex.LANE_HEALTH_FILE_NAME)
COMMIT_NAME = "Parsek run index"
COMMIT_EMAIL = "run-index@localhost"


# ---------------------------------------------------------------------------
# reading the archive
# ---------------------------------------------------------------------------

def _read_json(path: str) -> Tuple[Optional[object], Optional[str]]:
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh), None
    except FileNotFoundError:
        return None, "missing"
    except (OSError, ValueError) as exc:
        # The type only: an OSError's text holds the full path, and a row or a
        # skip reason must not carry a machine path.
        return None, type(exc).__name__


def scan_ksp_log(path: str) -> Tuple[Optional[runindex.LogScan], Optional[str]]:
    """Stream a gzipped (or plain) KSP.log through the scan. (scan, None), or
    (None, reason) when the file is missing or cannot be read to its end."""
    if not os.path.isfile(path):
        return None, "missing"
    scan = runindex.LogScan()
    opener = gzip.open if path.endswith(".gz") else open
    try:
        with opener(path, "rb") as fh:
            for line in fh:
                scan.feed(line)
    except (OSError, EOFError, zlib.error) as exc:
        return None, type(exc).__name__
    return scan, None


def read_run(archive_root: str, run_id: str, want_breakdown: bool = False
             ) -> Tuple[Optional[Dict], Optional[str], Optional[Dict]]:
    """(row, skip reason, per-tag breakdown) for one archived run folder."""
    run_dir = os.path.join(archive_root, run_id)
    if not os.path.isdir(run_dir):
        return None, "no archive folder", None
    result, err = _read_json(os.path.join(run_dir, run_id + ".json"))
    if result is None:
        return None, ("result json missing" if err == "missing" else "result json unreadable (%s)" % err), None
    manifest, merr = _read_json(os.path.join(run_dir, MANIFEST_NAME))
    gaps = []
    if manifest is None and merr != "missing":
        gaps.append("manifest unreadable (%s)" % merr)
    scan, serr = scan_ksp_log(os.path.join(run_dir, KSP_LOG_GZ))
    if scan is None:
        gaps.append("no KSP.log.gz" if serr == "missing" else "KSP.log.gz unreadable (%s)" % serr)
    row, reason = runindex.build_row(run_id, result, manifest if isinstance(manifest, dict) else None,
                                     scan.compact() if scan is not None else None, gaps)
    breakdown = scan.breakdown() if (scan is not None and want_breakdown) else None
    return row, reason, breakdown


def list_run_ids(archive_root: str) -> Tuple[List[str], List[str]]:
    """(run folder names, ignored temp / half-swapped folder names)."""
    try:
        names = sorted(os.listdir(archive_root))
    except OSError:
        return [], []
    runs, ignored = [], []
    for name in names:
        if not os.path.isdir(os.path.join(archive_root, name)):
            continue
        (ignored if name.startswith(".") else runs).append(name)
    return runs, ignored


def _scan_job(job: Tuple[str, str, bool]):
    archive_root, run_id, want = job
    try:
        row, reason, breakdown = read_run(archive_root, run_id, want)
    except Exception as exc:  # noqa: BLE001 - one bad folder must not stop a rebuild
        return run_id, None, "unexpected %s: %s" % (type(exc).__name__, exc), None
    return run_id, row, reason, breakdown


def _map_jobs(jobs: List[Tuple[str, str, bool]], workers: int, progress: Callable[[int, int], None]):
    total = len(jobs)
    if workers <= 1 or total < 2:
        for i, job in enumerate(jobs, 1):
            yield _scan_job(job)
            progress(i, total)
        return
    from concurrent.futures import ProcessPoolExecutor
    with ProcessPoolExecutor(max_workers=workers) as pool:
        for i, out in enumerate(pool.map(_scan_job, jobs, chunksize=8), 1):
            yield out
            progress(i, total)


def _cheap_rows(archive_root: str, run_ids: Sequence[str]) -> List[Dict]:
    """Rows without health, from the result JSONs only: enough to pick each
    lane's latest PASS before the logs are read."""
    rows = []
    for rid in run_ids:
        result, _err = _read_json(os.path.join(archive_root, rid, rid + ".json"))
        row, _reason = runindex.build_row(rid, result, None, None)
        if row is not None:
            rows.append(row)
    return rows


def index_archive(archive_root: str, run_ids: Sequence[str], workers: int = 1,
                  want_breakdown_for: Optional[set] = None,
                  progress: Optional[Callable[[int, int], None]] = None):
    """(rows, skipped [(runId, reason)], breakdowns {runId: per-tag}) for ``run_ids``."""
    want = want_breakdown_for or set()
    jobs = [(archive_root, rid, rid in want) for rid in run_ids]
    rows, skipped, breakdowns = [], [], {}
    for rid, row, reason, breakdown in _map_jobs(jobs, workers, progress or (lambda i, n: None)):
        if row is None:
            skipped.append((rid, reason))
            continue
        rows.append(row)
        if breakdown is not None:
            breakdowns[rid] = breakdown
    return rows, skipped, breakdowns


# ---------------------------------------------------------------------------
# files
# ---------------------------------------------------------------------------

def write_atomic(path: str, text: str) -> None:
    folder = os.path.dirname(os.path.abspath(path))
    os.makedirs(folder, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def load_index(path: str) -> "runindex.ParsedIndex":
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
    except FileNotFoundError:
        return runindex.ParsedIndex([], [], [])
    return runindex.parse_index(text)


def load_tag_map(path: Optional[str]) -> Tuple[Optional[Dict], str]:
    path = path or DEFAULT_TAG_MAP
    data, err = _read_json(path)
    if not isinstance(data, dict):
        return None, "tag map %s: %s (every tag reads unmapped)" % (path, err or "not an object")
    if data.get("schema") != runindex.TAG_MAP_SCHEMA:
        return None, "tag map %s has schema %r (every tag reads unmapped)" % (path, data.get("schema"))
    return data, "tag map %s: %s tags" % (path, (data.get("totals") or {}).get("tags"))


def load_spec_tiers(scenarios_dir: str) -> Optional[Dict[str, Optional[str]]]:
    """{scenario id: tier} of the committed specs (None when the folder is absent)."""
    if not os.path.isdir(scenarios_dir):
        return None
    try:
        import tomllib
    except ImportError:  # Python < 3.11: ids from file names, tiers unknown
        tomllib = None
    out: Dict[str, Optional[str]] = {}
    for name in sorted(os.listdir(scenarios_dir)):
        if not name.endswith(".toml"):
            continue
        sid, tier = name[:-5], None
        if tomllib is not None:
            try:
                with open(os.path.join(scenarios_dir, name), "rb") as fh:
                    spec = tomllib.load(fh)
                sid = spec.get("id") or sid
                tier = spec.get("tier")
            except (OSError, ValueError):
                pass
        out[sid] = tier
    return out


def append_runs(archive_root: str, run_ids: Sequence[str], index_path: Optional[str] = None
                ) -> Tuple[Dict[str, int], List[Tuple[str, str]], str]:
    """Index ``run_ids`` into ``index_path`` (default ``<archive>/index.jsonl``).
    Idempotent by run id. Returns (counts, skipped, index path); the counts
    also hold ``keptNewer`` (lines of a newer schema, kept in place, not read)
    and ``dropped`` (truncated or unreadable lines, dropped by this write;
    ``rebuild`` restores their runs from the archive). Raises on I/O failure;
    run.py turns that into one Warn line."""
    index_path = index_path or os.path.join(archive_root, runindex.INDEX_FILE_NAME)
    parsed = load_index(index_path)
    new_rows, skipped, _b = index_archive(archive_root, list(run_ids))
    merged, counts = runindex.upsert(parsed.rows, new_rows)
    if counts["added"] or counts["replaced"] or parsed.dropped or not os.path.exists(index_path):
        write_atomic(index_path, runindex.serialize_index(merged, parsed.newer))
    counts["skipped"] = len(skipped)
    counts["keptNewer"] = len(parsed.newer)
    counts["dropped"] = len(parsed.dropped)
    return counts, skipped, index_path


def format_append_counts(counts: Dict[str, int]) -> str:
    """``append_runs``' counts as one line (the CLI's and run.py's)."""
    text = "added=%d replaced=%d unchanged=%d skipped=%d" % (
        counts.get("added", 0), counts.get("replaced", 0), counts.get("unchanged", 0), counts.get("skipped", 0))
    if counts.get("keptNewer"):
        text += " keptNewerSchema=%d (lines a newer tool wrote, kept in place, not read)" % counts["keptNewer"]
    if counts.get("dropped"):
        text += " droppedUnreadable=%d (truncated or unparseable lines; rebuild restores their runs)" % counts["dropped"]
    return text


def build_health(archive_root: str, rows: Sequence[Dict], tag_map: Optional[Dict],
                 workers: int = 1, breakdowns: Optional[Dict[str, Dict]] = None
                 ) -> Tuple[Dict, List[Tuple[str, str]]]:
    """The lane-health document from each lane's latest PASS row."""
    latest = runindex.latest_pass_rows(rows)
    have = dict(breakdowns or {})
    need = [r["runId"] for r in latest.values() if r["runId"] not in have]
    missed: List[Tuple[str, str]] = []
    if need:
        _rows, skipped, got = index_archive(archive_root, need, workers, set(need))
        have.update(got)
        missed.extend(skipped)
    modules = runindex.tag_modules(tag_map)
    entries = {}
    for lane, row in latest.items():
        breakdown = have.get(row["runId"])
        if breakdown is None:
            if not any(rid == row["runId"] for rid, _r in missed):
                missed.append((row["runId"], "no readable KSP.log.gz"))
            continue
        entries[lane] = runindex.lane_health_entry(row, breakdown, modules)
    return runindex.build_lane_health(entries, tag_map), missed


# ---------------------------------------------------------------------------
# git: publish / fetch without a checkout of the branch
# ---------------------------------------------------------------------------

def _git(repo: str, args: Sequence[str], data: Optional[bytes] = None, check: bool = True,
         env: Optional[Dict[str, str]] = None) -> subprocess.CompletedProcess:
    full_env = None
    if env:
        full_env = dict(os.environ)
        full_env.update(env)
    proc = subprocess.run(["git", "-C", repo] + list(args), input=data, capture_output=True,
                          check=False, env=full_env)
    if check and proc.returncode != 0:
        raise RuntimeError("git %s failed (%d): %s" % (" ".join(args), proc.returncode,
                                                       (proc.stderr or proc.stdout).decode("utf-8", "replace").strip()))
    return proc


def remote_tip(repo: str, remote: str, branch: str) -> Optional[str]:
    """Fetch ``branch`` from ``remote`` and return its tip, or None when the
    remote has no such branch. Writes FETCH_HEAD only: no ref, no checkout."""
    probe = _git(repo, ["ls-remote", "--exit-code", "--heads", remote, branch], check=False)
    if probe.returncode == 2:
        return None
    if probe.returncode != 0:
        raise RuntimeError("git ls-remote %s %s failed: %s" % (
            remote, branch, probe.stderr.decode("utf-8", "replace").strip()))
    # --refmap= (empty): no opportunistic remote-tracking ref update either.
    _git(repo, ["fetch", "--quiet", "--no-tags", "--refmap=", remote, "+refs/heads/%s" % branch])
    return _git(repo, ["rev-parse", "--verify", "FETCH_HEAD^{commit}"]).stdout.decode().strip()


def read_blob(repo: str, commit: Optional[str], name: str) -> Optional[bytes]:
    if commit is None:
        return None
    proc = _git(repo, ["cat-file", "blob", "%s:%s" % (commit, name)], check=False)
    return proc.stdout if proc.returncode == 0 else None


def _commit_files(repo: str, parent: Optional[str], files: Dict[str, bytes], message: str) -> str:
    """One commit on ``parent`` whose tree is the parent's tree with only
    ``files`` (the tool's own, ``OWN_FILES``) added or replaced. Every other
    entry of the branch (a README.md, another tool's data, a folder) is carried
    over byte for byte: the tree is rebuilt from ``ls-tree`` of the parent."""
    stray = sorted(set(files) - set(OWN_FILES))
    if stray:
        raise RuntimeError("run index: refusing to write files it does not own: %s" % ", ".join(stray))
    entries: Dict[bytes, bytes] = {}
    if parent:
        listing = _git(repo, ["ls-tree", "-z", parent]).stdout
        for record in listing.split(b"\0"):
            if record:
                entries[record.split(b"\t", 1)[1]] = record
    for name in sorted(files):
        sha = _git(repo, ["hash-object", "-w", "--stdin"], data=files[name]).stdout.decode().strip()
        entries[name.encode("utf-8")] = ("100644 blob %s\t%s" % (sha, name)).encode("utf-8")
    data = b"".join(entries[k] + b"\0" for k in sorted(entries))
    # --missing: a submodule entry carried over names a commit this repo lacks.
    tree = _git(repo, ["mktree", "-z", "--missing"], data=data).stdout.decode().strip()
    args = ["commit-tree", tree, "-m", message]
    if parent:
        args[2:2] = ["-p", parent]
    env = {"GIT_AUTHOR_NAME": COMMIT_NAME, "GIT_AUTHOR_EMAIL": COMMIT_EMAIL,
           "GIT_COMMITTER_NAME": COMMIT_NAME, "GIT_COMMITTER_EMAIL": COMMIT_EMAIL}
    return _git(repo, args, env=env).stdout.decode().strip()


def _merged_files(local_rows: List[Dict], local_health: Optional[Dict], remote: Optional[str], repo: str,
                  local_newer: Sequence["runindex.NewerLine"] = ()) -> Tuple[Dict[str, bytes], int, int]:
    remote_index = runindex.ParsedIndex([], [], [])
    text = read_blob(repo, remote, runindex.INDEX_FILE_NAME)
    if text is not None:
        remote_index = runindex.parse_index(text.decode("utf-8", "replace"))
    remote_rows = remote_index.rows
    merged = runindex.merge_rows(local_rows, remote_rows)
    newer = runindex.merge_newer(local_newer, remote_index.newer)
    files = {BRANCH_README_NAME: BRANCH_README.encode("utf-8"),
             runindex.INDEX_FILE_NAME: runindex.serialize_index(merged, newer).encode("utf-8")}
    remote_health = None
    blob = read_blob(repo, remote, runindex.LANE_HEALTH_FILE_NAME)
    if blob is not None:
        try:
            remote_health = json.loads(blob.decode("utf-8"))
        except ValueError:
            remote_health = None
    health = runindex.merge_lane_health(local_health, remote_health)
    if health is not None:
        files[runindex.LANE_HEALTH_FILE_NAME] = runindex.serialize_lane_health(health).encode("utf-8")
    return files, len(merged), len(remote_rows)


def publish(repo: str, remote: str, branch: str, rows: List[Dict], health: Optional[Dict],
            attempts: int = 4, delay: float = 2.0,
            before_push: Optional[Callable[[int], None]] = None, log=print,
            newer: Sequence["runindex.NewerLine"] = ()) -> str:
    """Merge the local index (and lane health) with the branch's copy and push
    one commit on top of the remote tip. Returns "pushed", "created" or
    "unchanged". A rejected push is retried after a re-fetch and re-merge.
    Only ``OWN_FILES`` are written; every other file on the branch is kept."""
    if not branch:
        raise RuntimeError("--branch is required")
    for attempt in range(1, attempts + 1):
        tip = remote_tip(repo, remote, branch)
        files, merged_count, remote_count = _merged_files(rows, health, tip, repo, newer)
        if tip is not None and all(read_blob(repo, tip, n) == b for n, b in files.items()):
            log("run index: %s on %s already holds these %d rows; nothing to push"
                % (runindex.INDEX_FILE_NAME, branch, merged_count))
            return "unchanged"
        message = "run index: %d runs (%d from this machine, %d on the branch before)" % (
            merged_count, len(rows), remote_count)
        commit = _commit_files(repo, tip, files, message)
        if before_push is not None:
            before_push(attempt)
        push = _git(repo, ["push", "--quiet", remote, "%s:refs/heads/%s" % (commit, branch)], check=False)
        if push.returncode == 0:
            log("run index: pushed %s to %s (%d rows, attempt %d)" % (commit[:12], branch, merged_count, attempt))
            return "created" if tip is None else "pushed"
        reason = (push.stderr or push.stdout).decode("utf-8", "replace").strip().splitlines()
        log("run index: push attempt %d of %d rejected: %s" % (attempt, attempts, reason[-1] if reason else "?"))
        if attempt < attempts and delay:
            time.sleep(delay * attempt)
    raise RuntimeError("run index: could not push to %s after %d attempts" % (branch, attempts))


def fetch(repo: str, remote: str, branch: str) -> Tuple[Optional[bytes], Optional[bytes], Optional[str]]:
    tip = remote_tip(repo, remote, branch)
    if tip is None:
        return None, None, None
    return read_blob(repo, tip, runindex.INDEX_FILE_NAME), read_blob(repo, tip, runindex.LANE_HEALTH_FILE_NAME), tip


# ---------------------------------------------------------------------------
# printing
# ---------------------------------------------------------------------------

def _short(sha: Optional[str], n: int = 12) -> str:
    return sha[:n] if sha else "-"


def _print_json(data) -> None:
    print(json.dumps(data, indent=1, sort_keys=True))


def _fmt_brief(b: Optional[Dict]) -> str:
    if not b:
        return "none"
    v = b.get("verdict") or "?"
    if b.get("subkind"):
        v += "(%s)" % b["subkind"]
    return "%s %s %s commit=%s dll=%s wall=%ss" % ((b.get("started") or "?"), b.get("runId"), v,
                                                 _short(b.get("commit")), _short(b.get("dll"), 16), b.get("wall"))


def print_lane(rep: Dict) -> None:
    print("lane %s (tier %s; windows end %s)" % (rep["lane"], rep.get("tier"), rep.get("now")))
    print("  last run:  %s" % _fmt_brief(rep.get("lastRun")))
    print("  last PASS: %s" % _fmt_brief(rep.get("lastPass")))
    for name in ("30d", "90d", "all"):
        c = rep["windows"][name]
        rate = "-" if c["flakeRate"] is None else "%.0f%%" % (100 * c["flakeRate"])
        print("  %-4s attempts=%d pass=%d fail=%d invalid=%d  scenario runs=%d final pass=%d flaked=%d (%s)"
              % (name, c["attempts"], c["passes"], c["fails"], c["invalids"], c["scenarioRuns"],
                 c["finalPasses"], c["flaked"], rate))
    print("  wall p50=%s p90=%s; last %d: %s" % (rep.get("wallP50"), rep.get("wallP90"), len(rep["lastWalls"]),
                                                 " ".join("%s%s" % (w, "" if v == "PASS" else "(%s)" % v)
                                                          for _r, v, w in rep["lastWalls"])))


def print_summary(s: Dict) -> None:
    print("run index: %d attempts over %d lanes (newest %s)" % (s["attempts"], s["lanes"], s.get("now")))
    print("  by verdict: %s" % ", ".join("%s %d" % kv for kv in s["byVerdict"].items()))
    for tier, counts in s["byTier"].items():
        print("  tier %-9s %s" % (tier, ", ".join("%s %d" % kv for kv in counts.items())))
    print("  never passed (%d): %s" % (len(s["neverPassed"]), ", ".join(
        "%s(%s x%d)" % (x["lane"], x["lastVerdict"], x["attempts"]) for x in s["neverPassed"]) or "-"))
    print("  last run not PASS (%d):" % len(s["lastNotPass"]))
    for x in s["lastNotPass"]:
        print("    %s %s %s%s" % (x["lane"], x["runId"], x["verdict"], "(%s)" % x["subkind"] if x["subkind"] else ""))
    print("  flakiest (%d shown):" % len(s["flakiest"]))
    for x in s["flakiest"]:
        print("    %s flaked %d of %d scenario runs" % (x["lane"], x["flaked"], x["scenarioRuns"]))
    print("  slowest by p50 wall (%d shown):" % len(s["slowest"]))
    for x in s["slowest"]:
        print("    %s p50=%ss p90=%ss over %d" % (x["lane"], x["wallP50"], x["wallP90"], x["attempts"]))
    if "specsWithNoRun" in s:
        print("  specs with no run (%d): %s" % (len(s["specsWithNoRun"]), " ".join(s["specsWithNoRun"]) or "-"))
        print("  lanes in the index with no spec (%d): %s" % (len(s["lanesWithNoSpec"]),
                                                             " ".join(s["lanesWithNoSpec"]) or "-"))


def _report_skipped(skipped: Sequence[Tuple[str, str]], ignored: Sequence[str] = ()) -> None:
    if ignored:
        print("  ignored %d temp folder(s): %s" % (len(ignored), " ".join(ignored)))
    if not skipped:
        return
    by_reason: Dict[str, List[str]] = {}
    for rid, reason in skipped:
        by_reason.setdefault(reason or "?", []).append(rid)
    print("  skipped %d run folder(s):" % len(skipped))
    for reason in sorted(by_reason):
        print("    %s (%d): %s" % (reason, len(by_reason[reason]), " ".join(sorted(by_reason[reason]))))


def _report_gaps(rows: Sequence[Dict]) -> None:
    gaps: Dict[str, int] = {}
    for r in rows:
        for g in r.get("gaps") or ():
            key = g.split(" (")[0]
            gaps[key] = gaps.get(key, 0) + 1
    if gaps:
        print("  indexed with gaps: %s" % ", ".join("%s %d" % kv for kv in sorted(gaps.items())))


def _sizes(rows: Sequence[Dict], text: str) -> str:
    n = len(rows)
    size = len(text.encode("utf-8"))
    return "%d bytes, %d per row" % (size, size // n if n else 0)


# ---------------------------------------------------------------------------
# commands
# ---------------------------------------------------------------------------

def _archive(args) -> str:
    return os.path.abspath(args.archive or DEFAULT_ARCHIVE_DIR)


def _index_path(args, attr: str = "index") -> str:
    value = getattr(args, attr, None)
    return os.path.abspath(value) if value else os.path.join(_archive(args), runindex.INDEX_FILE_NAME)


def _health_path(args, attr: str = "health") -> str:
    value = getattr(args, attr, None)
    return os.path.abspath(value) if value else os.path.join(_archive(args), runindex.LANE_HEALTH_FILE_NAME)


def _need_index(path: str) -> "runindex.ParsedIndex":
    parsed = load_index(path)
    for p in parsed.dropped:
        print("run_index: %s: %s (skipped)" % (path, p), file=sys.stderr)
    if parsed.newer:
        print("run_index: %s: %d line(s) of a newer index schema kept, not read"
              % (path, len(parsed.newer)), file=sys.stderr)
    if not parsed.rows:
        raise SystemExit("run_index: no rows in %s" % path)
    return parsed


def _need_rows(path: str) -> List[Dict]:
    return _need_index(path).rows


def cmd_rebuild(args) -> int:
    archive = _archive(args)
    if not os.path.isdir(archive):
        print("run_index: archive %s does not exist" % archive, file=sys.stderr)
        return 1
    run_ids, ignored = list_run_ids(archive)
    want = set()
    if args.health_out:
        want = {r["runId"] for r in runindex.latest_pass_rows(_cheap_rows(archive, run_ids)).values()}
    started = time.time()

    def progress(i, n):
        if args.progress and (i % args.progress == 0 or i == n):
            print("  %d / %d (%.0fs)" % (i, n, time.time() - started), flush=True)

    rows, skipped, breakdowns = index_archive(archive, run_ids, args.jobs, want, progress)
    out = os.path.abspath(args.out) if args.out else os.path.join(archive, runindex.INDEX_FILE_NAME)
    # A newer tool's lines in the index being replaced survive the rebuild.
    kept = load_index(out).newer
    text = runindex.serialize_index(rows, kept)
    write_atomic(out, text)
    print("run index: %d run folder(s), %d indexed, %d skipped, %d newer-schema line(s) kept -> %s (%s) in %.0fs"
          % (len(run_ids), len(rows), len(skipped), len(kept), out, _sizes(rows, text), time.time() - started))
    _report_skipped(skipped, ignored)
    _report_gaps(rows)
    if args.health_out:
        tag_map, note = load_tag_map(args.tag_map)
        print("  %s" % note)
        doc, missed = build_health(archive, runindex.normalize(rows), tag_map, args.jobs, breakdowns)
        htext = runindex.serialize_lane_health(doc)
        write_atomic(os.path.abspath(args.health_out), htext)
        print("lane health: %d lane(s), %d unmapped tag(s) -> %s (%d bytes)" % (
            len(doc["lanes"]), len(doc["unmappedTags"]), args.health_out, len(htext.encode("utf-8"))))
        _report_skipped(missed)
    return 0


def cmd_append(args) -> int:
    archive = _archive(args)
    counts, skipped, path = append_runs(archive, args.run, _index_path(args))
    print("run index: %s -> %s" % (format_append_counts(counts), path))
    _report_skipped(skipped)
    return 1 if skipped else 0


def cmd_lane(args) -> int:
    rows = _need_rows(_index_path(args))
    rep = runindex.lane_report(rows, args.lane, args.now)
    if not rep["lastRun"]:
        print("run_index: lane %s has no run in the index" % args.lane, file=sys.stderr)
        return 1
    _print_json(rep) if args.json else print_lane(rep)
    return 0


def cmd_summary(args) -> int:
    rows = _need_rows(_index_path(args))
    s = runindex.summary(rows, load_spec_tiers(args.scenarios), args.top)
    _print_json(s) if args.json else print_summary(s)
    return 0


def cmd_lane_table(args) -> int:
    rows = _need_rows(_index_path(args))
    sys.stdout.write(runindex.lane_table(rows, load_spec_tiers(args.scenarios) if args.with_specs else None))
    return 0


def cmd_trend(args) -> int:
    rows = _need_rows(_index_path(args))
    points = runindex.trend(rows, args.lane, args.last)
    if args.json:
        _print_json(points)
        return 0
    print("%-20s %-12s %6s %6s %6s %8s %10s %s" % ("started", "verdict", "wall", "warn", "error", "parsek",
                                                   "logBytes", "commit"))
    for p in points:
        print("%-20s %-12s %6s %6s %6s %8s %10s %s" % (p["started"], p["verdict"], p["wall"], p["warn"],
                                                       p["error"], p["parsek"], p["bytes"], _short(p["commit"], 9)))
    return 0


def cmd_regressions(args) -> int:
    rows = _need_rows(_index_path(args))
    found = runindex.health_regressions(rows)
    if args.json:
        _print_json(found)
        return 0
    print("health regressions (report-only): %d lane(s)" % len(found))
    for f in found:
        print("  %s %s commit=%s: %s" % (f["lane"], f["runId"], _short(f["commit"], 9), "; ".join(f["reasons"])))
    return 0


def cmd_health_build(args) -> int:
    rows = runindex.normalize(_need_rows(_index_path(args)))
    tag_map, note = load_tag_map(args.tag_map)
    print(note)
    doc, missed = build_health(_archive(args), rows, tag_map, args.jobs)
    text = runindex.serialize_lane_health(doc)
    out = _health_path(args, "out")
    write_atomic(out, text)
    print("lane health: %d lane(s), %d unmapped tag(s) -> %s (%d bytes)" % (
        len(doc["lanes"]), len(doc["unmappedTags"]), out, len(text.encode("utf-8"))))
    _report_skipped(missed)
    return 0


def cmd_lanes_for(args) -> int:
    data, err = _read_json(_health_path(args))
    if not isinstance(data, dict):
        print("run_index: lane health %s: %s" % (_health_path(args), err), file=sys.stderr)
        return 1
    found = runindex.lanes_for(data, args.module, args.subsystem)
    if args.json:
        _print_json(found)
        return 0
    what = ("module %s" % args.module) if args.module else ("subsystem %s" % args.subsystem)
    print("%d lane(s) log %s (latest PASS of each):" % (len(found), what))
    for f in found:
        tail = " (only through a tag shared with another module)" if f.get("sharedTagOnly") else \
            " warn=%s error=%s" % (f["warn"], f["error"])
        print("  %-48s %7d lines%s" % (f["lane"], f["lines"], tail))
    return 0


def cmd_publish(args) -> int:
    parsed = _need_index(os.path.abspath(args.index))
    rows = parsed.rows
    health = None
    if args.health:
        health, err = _read_json(os.path.abspath(args.health))
        if not isinstance(health, dict):
            print("run_index: lane health %s: %s" % (args.health, err), file=sys.stderr)
            return 1
    try:
        publish(os.path.abspath(args.repo), args.remote, args.branch, runindex.normalize(rows), health,
                args.attempts, args.delay, newer=parsed.newer)
    except RuntimeError as exc:
        print("run_index: %s" % exc, file=sys.stderr)
        return 1
    return 0


def cmd_fetch(args) -> int:
    try:
        index, health, tip = fetch(os.path.abspath(args.repo), args.remote, args.branch)
    except RuntimeError as exc:
        print("run_index: %s" % exc, file=sys.stderr)
        return 1
    if tip is None:
        print("run_index: %s has no branch %s" % (args.remote, args.branch), file=sys.stderr)
        return 1
    if index is None:
        print("run_index: %s:%s holds no %s" % (args.branch, tip[:12], runindex.INDEX_FILE_NAME), file=sys.stderr)
        return 1
    write_atomic(os.path.abspath(args.out), index.decode("utf-8"))
    parsed = runindex.parse_index(index.decode("utf-8", "replace"))
    print("run index: %d rows (%d newer-schema line(s) kept) from %s at %s -> %s"
          % (len(parsed.rows), len(parsed.newer), args.branch, tip[:12], args.out))
    if args.health_out and health is not None:
        write_atomic(os.path.abspath(args.health_out), health.decode("utf-8"))
        print("lane health -> %s" % args.health_out)
    return 0


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description="Harness run index and per-lane runtime health "
                                             "(harness/lib/runindex.py documents the data).")
    sub = ap.add_subparsers(dest="command", required=True)

    def archive_arg(p):
        p.add_argument("--archive", default=None,
                       help="results archive (default <umbrella>/automation/results-archive)")

    def index_arg(p):
        archive_arg(p)
        p.add_argument("--index", default=None, help="index file (default <archive>/index.jsonl)")

    p = sub.add_parser("rebuild", help="index every run folder of the archive")
    archive_arg(p)
    p.add_argument("--out", default=None, help="index file to write (default <archive>/index.jsonl)")
    p.add_argument("--health-out", default=None, help="also write the lane-health file here")
    p.add_argument("--tag-map", default=None, help="tag -> module map (default scripts/arch/log_tags.json)")
    p.add_argument("--jobs", type=int, default=1, help="worker processes reading the logs (default 1)")
    p.add_argument("--progress", type=int, default=0, help="print progress every N runs")
    p.set_defaults(func=cmd_rebuild)

    p = sub.add_parser("append", help="index one run (idempotent on run id)")
    index_arg(p)
    p.add_argument("--run", action="append", required=True, help="run id (repeatable)")
    p.set_defaults(func=cmd_append)

    p = sub.add_parser("lane", help="last run, last PASS, counts, flake rate and wall time of one lane")
    index_arg(p)
    p.add_argument("lane")
    p.add_argument("--now", default=None, help="end of the 30/90-day windows (default: newest run in the index)")
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_lane)

    p = sub.add_parser("summary", help="totals, never-passed, last-not-PASS, flakiest, slowest, specs with no run")
    index_arg(p)
    p.add_argument("--scenarios", default=DEFAULT_SCENARIOS_DIR, help="spec folder (default harness/scenarios)")
    p.add_argument("--top", type=int, default=20)
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_summary)

    p = sub.add_parser("lane-table", help="Markdown table of every lane to stdout")
    index_arg(p)
    p.add_argument("--scenarios", default=DEFAULT_SCENARIOS_DIR)
    p.add_argument("--with-specs", action="store_true", help="add a row for every spec with no run")
    p.set_defaults(func=cmd_lane_table)

    p = sub.add_parser("trend", help="warnings, errors, log volume and wall time over a lane's last runs")
    index_arg(p)
    p.add_argument("lane")
    p.add_argument("--last", type=int, default=10)
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_trend)

    p = sub.add_parser("health-regressions", help="lanes whose latest PASS regressed against their previous five")
    index_arg(p)
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_regressions)

    p = sub.add_parser("health-build", help="write the lane-health file from the index and the archive")
    index_arg(p)
    p.add_argument("--out", default=None, help="default <archive>/lane-health.json")
    p.add_argument("--tag-map", default=None)
    p.add_argument("--jobs", type=int, default=1)
    p.set_defaults(func=cmd_health_build)

    p = sub.add_parser("lanes-for", help="lanes whose latest PASS logs a module or a subsystem tag")
    archive_arg(p)
    p.add_argument("--health", default=None, help="lane-health file (default <archive>/lane-health.json)")
    group = p.add_mutually_exclusive_group(required=True)
    group.add_argument("--module")
    group.add_argument("--subsystem")
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_lanes_for)

    p = sub.add_parser("publish", help="merge the index into a git branch and push (no checkout needed)")
    p.add_argument("--index", required=True)
    p.add_argument("--health", default=None, help="lane-health file to publish beside it")
    p.add_argument("--repo", required=True, help="any checkout or worktree of the repository")
    p.add_argument("--remote", required=True)
    p.add_argument("--branch", required=True, help="the data branch (no default: the owner names it)")
    p.add_argument("--attempts", type=int, default=4)
    p.add_argument("--delay", type=float, default=2.0)
    p.set_defaults(func=cmd_publish)

    p = sub.add_parser("fetch", help="read the index (and lane health) off a git branch")
    p.add_argument("--repo", required=True)
    p.add_argument("--remote", required=True)
    p.add_argument("--branch", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--health-out", default=None)
    p.set_defaults(func=cmd_fetch)
    return ap


def main(argv: Optional[List[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except BrokenPipeError:  # output piped into `head`
        os.dup2(os.open(os.devnull, os.O_WRONLY), sys.stdout.fileno())
        sys.exit(0)
