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
    python harness/tools/run_index.py import-snapshots --from LOGS_DIR [--archive DIR] [--dry-run] [--list] [--append]
    python harness/tools/run_index.py lane LANE [--index FILE] [--now ISO] [--json]
    python harness/tools/run_index.py summary [--index FILE] [--json]
    python harness/tools/run_index.py lane-table [--index FILE]
    python harness/tools/run_index.py trend LANE [--last N] [--index FILE]
    python harness/tools/run_index.py health-regressions [--index FILE]
    python harness/tools/run_index.py health-build [--index FILE] [--archive DIR] [--out FILE]
    python harness/tools/run_index.py lanes-for (--module M | --subsystem S) [--min-lines 5] [--health FILE]
    python harness/tools/run_index.py publish --index FILE [--health FILE] --repo DIR --remote R --branch metrics [--dir flights]
    python harness/tools/run_index.py fetch --repo DIR --remote R --branch metrics [--dir flights] --out FILE [--health-out FILE]

Defaults: ``--archive`` is ``<umbrella>/automation/results-archive`` (the
umbrella is this worktree's parent), ``--index`` / ``--out`` is
``<archive>/index.jsonl``, ``--health`` is ``<archive>/lane-health.json``.
``publish`` / ``fetch`` take ``--branch`` with no default; the owner's ruling of
2026-11-12 names ``metrics``, with the flight files in its ``flights/`` folder
(``--dir``), apart from the unit-test metrics CI appends at the branch root.

Nothing here changes a run's verdict. run.py calls ``append_runs`` after it
archives a scenario's attempts and logs one line whatever happens.
"""

from __future__ import annotations

import argparse
import gzip
import json
import os
import re
import shutil
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

This folder holds the harness run index (the flight results; the unit-test metrics are
`metrics.jsonl` at the branch root), written by `harness/tools/run_index.py publish`
from the machine-wide results archive (`<umbrella>/automation/results-archive/`):

- `index.jsonl`: one JSON line per archived run attempt (schema `parsek-run-index/1`).
- `lane-health.json`: per lane, from its latest PASS, Parsek log lines per subsystem
  and per module (schema `parsek-lane-health/1`).

`harness/lib/runindex.py` documents every key. Read it with
`python harness/tools/run_index.py fetch --repo . --remote origin --branch metrics --out index.jsonl`
and then `run_index.py lane <LANE> --index index.jsonl`. Publishing merges by run id
(and by lane), so two machines may both publish. Each publish is one commit whose
subject starts with `flights: `, so `git log -- flights/` is the flight history alone.
Publishing writes only the three files of this folder; every other file on the branch
is kept. Do not edit these three by hand.
"""
# Where the flight files live on the data branch (owner ruling 2026-11-12: the
# `metrics` branch, kept apart from the unit-test metrics at its root). An empty
# folder is the branch root, the first layout, still read by `fetch`.
DEFAULT_BRANCH_DIR = "flights"
COMMIT_SUBJECT_PREFIX = "flights: "
# The root-layout files: the readme had its own name there so the branch's own
# README.md was never touched.
ROOT_README_NAME = "RUN-INDEX.md"
LEGACY_ROOT_FILES = (ROOT_README_NAME, runindex.INDEX_FILE_NAME, runindex.LANE_HEALTH_FILE_NAME)
_DIR_PART_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")


def branch_dir(value: Optional[str]) -> str:
    """The ``--dir`` value normalized (``flights/`` -> ``flights``; "" the root).
    Raises on a part that is not a plain folder name (no ``..``, no absolute path)."""
    parts = [p for p in re.split(r"[\\/]+", value or "") if p]
    for part in parts:
        if not _DIR_PART_RE.match(part):
            raise RuntimeError("--dir %r: %r is not a plain folder name" % (value, part))
    return "/".join(parts)


def own_files(directory: str) -> Tuple[str, str, str]:
    """(readme, index, lane health) paths on the branch for ``directory``."""
    if not directory:
        return LEGACY_ROOT_FILES
    return tuple("%s/%s" % (directory, name)
                 for name in ("README.md", runindex.INDEX_FILE_NAME, runindex.LANE_HEALTH_FILE_NAME))


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


def read_snapshot_run(run_dir: str, run_id: str, want_breakdown: bool = False
                      ) -> Tuple[Optional[Dict], Optional[str], Optional[Dict]]:
    """(row, skip reason, per-tag breakdown) for a folder ``import-snapshots``
    wrote: its ``snapshot.json`` marker and ``KSP.log.gz``."""
    marker, err = _read_json(os.path.join(run_dir, runindex.SNAPSHOT_FILE_NAME))
    if marker is None:
        return None, "snapshot json unreadable (%s)" % err, None
    gaps = []
    scan, serr = scan_ksp_log(os.path.join(run_dir, KSP_LOG_GZ))
    if scan is None:
        gaps.append("no KSP.log.gz" if serr == "missing" else "KSP.log.gz unreadable (%s)" % serr)
    row, reason = runindex.build_snapshot_row(run_id, marker, scan.compact() if scan is not None else None, gaps)
    breakdown = scan.breakdown() if (scan is not None and want_breakdown) else None
    return row, reason, breakdown


def read_run(archive_root: str, run_id: str, want_breakdown: bool = False
             ) -> Tuple[Optional[Dict], Optional[str], Optional[Dict]]:
    """(row, skip reason, per-tag breakdown) for one archived run folder: a
    run.py folder (``<runId>.json``) or an imported snapshot (``snapshot.json``
    and no result JSON)."""
    run_dir = os.path.join(archive_root, run_id)
    if not os.path.isdir(run_dir):
        return None, "no archive folder", None
    result, err = _read_json(os.path.join(run_dir, run_id + ".json"))
    if result is None and err == "missing" and os.path.isfile(os.path.join(run_dir, runindex.SNAPSHOT_FILE_NAME)):
        return read_snapshot_run(run_dir, run_id, want_breakdown)
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
# import-snapshots: collect-logs snapshot folders into the archive
# ---------------------------------------------------------------------------

SNAPSHOT_TMP_SUFFIX = ".snapshot-tmp"
LOG_VALIDATION_NAME = "log-validation.txt"
GIT_STATE_NAME = "git-state.txt"
SNAPSHOT_LOG_NAME = "KSP.log"


def _read_text(path: str) -> Optional[str]:
    """The file's text (undecodable bytes replaced), or None when absent."""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except FileNotFoundError:
        return None


def archive_state(archive_root: str) -> "runindex.ArchiveState":
    """What the archive holds, for ``decide_snapshot``. A folder with no result
    JSON and no readable snapshot marker counts as a run (it may be a run whose
    JSON was lost), so its minute blocks an import rather than invite a twin."""
    state = runindex.ArchiveState()
    run_ids, _ignored = list_run_ids(archive_root)
    for rid in run_ids:
        run_dir = os.path.join(archive_root, rid)
        marker_path = os.path.join(run_dir, runindex.SNAPSHOT_FILE_NAME)
        if os.path.isfile(os.path.join(run_dir, rid + ".json")) or not os.path.isfile(marker_path):
            state.add_run(rid)
            continue
        marker, _err = _read_json(marker_path)
        if isinstance(marker, dict) and marker.get("schema") == runindex.SNAPSHOT_SCHEMA:
            state.add_snapshot(rid, marker.get("lane"), marker.get("runUtc"))
        else:
            state.add_run(rid)
    return state


def snapshot_facts(log_path: str) -> "runindex.SnapshotLogFacts":
    facts = runindex.SnapshotLogFacts()
    with open(log_path, "rb") as fh:
        for line in fh:
            facts.feed(line)
    return facts


def write_snapshot_folder(archive_root: str, run_id: str, log_path: str, marker: Dict) -> None:
    """Write ``<archive>/<run_id>/`` (KSP.log.gz + snapshot.json) through a
    hidden temp folder and one rename. Never replaces an existing folder."""
    final = os.path.join(archive_root, run_id)
    if os.path.lexists(final):
        raise FileExistsError("archive folder exists")
    tmp = os.path.join(archive_root, "." + run_id + SNAPSHOT_TMP_SUFFIX)
    if os.path.isdir(tmp):  # an interrupted import of this very run: ours to redo
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    with open(log_path, "rb") as src, gzip.open(os.path.join(tmp, KSP_LOG_GZ), "wb") as dst:
        shutil.copyfileobj(src, dst, 1 << 20)
    with open(os.path.join(tmp, runindex.SNAPSHOT_FILE_NAME), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(runindex.serialize_marker(marker))
    if os.path.lexists(final):
        shutil.rmtree(tmp)
        raise FileExistsError("archive folder exists")
    os.rename(tmp, final)


def import_snapshots(logs_dir: str, archive_root: str, dry_run: bool = False
                     ) -> Tuple[Dict[Tuple[str, str], int], List[Tuple[str, str, str, Optional[str]]], Dict[str, int]]:
    """Import every harness snapshot folder of ``logs_dir`` into the archive.
    Returns ({(outcome, reason): count}, [(folder, outcome, reason, runId)],
    facts counted over the imports plus ``matchedVerdicts``: the archived
    verdicts of the runs an ``already-archived`` snapshot matched).
    ``dry_run`` writes nothing and decides exactly as a real run would (later
    folders see earlier imports). A write that fails is undone in the
    decision state too, so a second copy of the same run can still import."""
    state = archive_state(archive_root)
    seen: set = set()
    counts: Dict[Tuple[str, str], int] = {}
    decisions: List[Tuple[str, str, str, Optional[str]]] = []
    facts_count = {"withCommit": 0, "dirty": 0, "withDll": 0, "withProfile": 0}
    # The archived verdicts of the runs a snapshot matched: the check that the
    # harness collected logs for non-PASS runs only (a PASS here says otherwise).
    matched: Dict[str, int] = {}
    for name in sorted(os.listdir(logs_dir)):
        folder = os.path.join(logs_dir, name)
        if not os.path.isdir(folder):
            continue
        try:
            log_path = os.path.join(folder, SNAPSHOT_LOG_NAME)
            has_log = os.path.isfile(log_path)
            validation = _read_text(os.path.join(folder, LOG_VALIDATION_NAME))
            git_text = _read_text(os.path.join(folder, GIT_STATE_NAME))
            m = runindex.SNAPSHOT_FOLDER_RE.match(name)
            # Read the (large) log only when the cheap facts leave it in play.
            worth = has_log and m is not None and runindex._TOKEN_RE.match(m.group(3)) \
                and runindex.classify_log_path(validation)[0] == "harness"
            facts = snapshot_facts(log_path) if worth else runindex.SnapshotLogFacts()
            d = runindex.decide_snapshot(name, has_log, validation, git_text, facts, state, seen)
            if d.outcome == "import" and not dry_run:
                try:
                    write_snapshot_folder(archive_root, d.run_id, log_path, d.marker)
                except OSError:
                    seen.discard((d.marker["lane"], d.marker["runUtc"]))
                    state.snapshots.pop(d.run_id, None)
                    state.taken.discard(d.run_id)
                    raise
        except OSError as exc:
            d = runindex.SnapshotDecision("skip", "unreadable (%s)" % type(exc).__name__)
        key = (d.outcome, d.reason)
        counts[key] = counts.get(key, 0) + 1
        decisions.append((name, d.outcome, d.reason, d.run_id))
        if d.reason == "already-archived" and d.run_id:
            result, _err = _read_json(os.path.join(archive_root, d.run_id, d.run_id + ".json"))
            verdict = result.get("verdict") if isinstance(result, dict) else None
            key = verdict if isinstance(verdict, str) and runindex._TOKEN_RE.match(verdict) else "?"
            matched[key] = matched.get(key, 0) + 1
        if d.outcome == "import":
            mk = d.marker
            facts_count["withCommit"] += 1 if mk.get("commit") else 0
            facts_count["dirty"] += 1 if mk.get("dirty") else 0
            facts_count["withDll"] += 1 if mk.get("dll") else 0
            facts_count["withProfile"] += 1 if mk.get("profile") else 0
    facts_count["matchedVerdicts"] = dict(sorted(matched.items()))
    return counts, decisions, facts_count


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


def _build_tree(repo: str, tree: Optional[str], changes: Dict[str, Optional[bytes]]) -> Optional[str]:
    """``tree`` (None: empty) with ``changes`` applied: a path -> bytes writes a
    100644 blob, a path -> None removes it; a path may be nested
    (``flights/index.jsonl``). Every other entry, at the root and in each
    folder, is carried over byte for byte with its mode (``ls-tree`` records
    fed back to ``mktree``). None when the result is empty."""
    entries: Dict[bytes, bytes] = {}
    if tree:
        listing = _git(repo, ["ls-tree", "-z", tree]).stdout
        for record in listing.split(b"\0"):
            if record:
                entries[record.split(b"\t", 1)[1]] = record
    nested: Dict[str, Dict[str, Optional[bytes]]] = {}
    for path, data in changes.items():
        head, _sep, rest = path.partition("/")
        if rest:
            nested.setdefault(head, {})[rest] = data
            continue
        key = head.encode("utf-8")
        if data is None:
            entries.pop(key, None)
            continue
        old = entries.get(key)
        if old is not None and not old.startswith(b"100644 blob ") and not old.startswith(b"100755 blob "):
            raise RuntimeError("run index: %s on the branch is not a file; refusing to replace it" % path)
        sha = _git(repo, ["hash-object", "-w", "--stdin"], data=data).stdout.decode().strip()
        entries[key] = ("100644 blob %s\t%s" % (sha, head)).encode("utf-8")
    for head, sub in nested.items():
        key = head.encode("utf-8")
        old = entries.get(key)
        sub_tree = None
        if old is not None:
            meta = old.split(b"\t", 1)[0].split(b" ")
            if meta[1] != b"tree":
                raise RuntimeError("run index: %s on the branch is not a folder; refusing to replace it" % head)
            sub_tree = meta[2].decode()
        new = _build_tree(repo, sub_tree, sub)
        if new is None:
            entries.pop(key, None)
        else:
            entries[key] = ("040000 tree %s\t%s" % (new, head)).encode("utf-8")
    if not entries:
        return None
    data = b"".join(entries[k] + b"\0" for k in sorted(entries))
    # --missing: a submodule entry carried over names a commit this repo lacks.
    return _git(repo, ["mktree", "-z", "--missing"], data=data).stdout.decode().strip()


def _commit_files(repo: str, parent: Optional[str], files: Dict[str, Optional[bytes]], message: str,
                  directory: str = DEFAULT_BRANCH_DIR) -> str:
    """One commit on ``parent`` whose tree is the parent's tree with only the
    tool's own entries changed: the three files of ``directory`` written, and
    (only when ``directory`` is not the root) the three root-layout files
    removed. Every other entry of the branch (the root README.md, CI's
    metrics.jsonl, another file in the folder) is carried over byte for byte.
    Any other path is refused."""
    allowed_writes = set(own_files(directory))
    allowed_removals = set(LEGACY_ROOT_FILES) if directory else set()
    stray = sorted(p for p, d in files.items()
                   if p not in (allowed_writes if d is not None else allowed_removals))
    if stray:
        raise RuntimeError("run index: refusing to write files it does not own: %s" % ", ".join(stray))
    tree = _build_tree(repo, _git(repo, ["rev-parse", "%s^{tree}" % parent]).stdout.decode().strip()
                       if parent else None, files)
    if tree is None:
        raise RuntimeError("run index: refusing to commit an empty tree")
    args = ["commit-tree", tree, "-m", message]
    if parent:
        args[2:2] = ["-p", parent]
    env = {"GIT_AUTHOR_NAME": COMMIT_NAME, "GIT_AUTHOR_EMAIL": COMMIT_EMAIL,
           "GIT_COMMITTER_NAME": COMMIT_NAME, "GIT_COMMITTER_EMAIL": COMMIT_EMAIL}
    return _git(repo, args, env=env).stdout.decode().strip()


def _parse_health(blob: Optional[bytes]) -> Optional[Dict]:
    if blob is None:
        return None
    try:
        doc = json.loads(blob.decode("utf-8"))
    except ValueError:
        return None
    return doc if isinstance(doc, dict) else None


def _is_run_index_text(blob: Optional[bytes]) -> bool:
    """True when every line is a run index line (rows or newer-schema lines)."""
    if blob is None:
        return False
    parsed = runindex.parse_index(blob.decode("utf-8", "replace"))
    return not parsed.dropped and bool(parsed.rows or parsed.newer)


def legacy_root_files(repo: str, tip: Optional[str]) -> Dict[str, bytes]:
    """The root-layout files this tool wrote that the tip still holds: each
    checked to be the tool's own (the readme's heading, the index's lines, the
    lane-health schema), so a same-named file of anyone else is never taken."""
    found: Dict[str, bytes] = {}
    readme = read_blob(repo, tip, ROOT_README_NAME)
    if readme is not None and readme.startswith(b"# Parsek harness run index"):
        found[ROOT_README_NAME] = readme
    index = read_blob(repo, tip, runindex.INDEX_FILE_NAME)
    if _is_run_index_text(index):
        found[runindex.INDEX_FILE_NAME] = index
    health = _parse_health(read_blob(repo, tip, runindex.LANE_HEALTH_FILE_NAME))
    if health is not None and str(health.get("schema", "")).startswith("parsek-lane-health/"):
        found[runindex.LANE_HEALTH_FILE_NAME] = read_blob(repo, tip, runindex.LANE_HEALTH_FILE_NAME)
    return found


def _merged_files(local_rows: List[Dict], local_health: Optional[Dict], remote: Optional[str], repo: str,
                  local_newer: Sequence["runindex.NewerLine"] = (), directory: str = DEFAULT_BRANCH_DIR
                  ) -> Tuple[Dict[str, Optional[bytes]], int, int]:
    """The tool's changes for one publish: its three files in ``directory``,
    the branch's copies (and, outside the root, the root-layout ones it finds)
    merged in, and the root-layout files removed."""
    readme_path, index_path, health_path = own_files(directory)
    legacy = legacy_root_files(repo, remote) if directory else {}
    remote_rows: List[Dict] = []
    remote_newer: List["runindex.NewerLine"] = []
    for blob in (read_blob(repo, remote, index_path), legacy.get(runindex.INDEX_FILE_NAME)):
        if blob is not None:
            parsed = runindex.parse_index(blob.decode("utf-8", "replace"))
            remote_rows = list(runindex.merge_rows(remote_rows, parsed.rows))
            remote_newer = runindex.merge_newer(remote_newer, parsed.newer)
    merged = runindex.merge_rows(local_rows, remote_rows)
    newer = runindex.merge_newer(local_newer, remote_newer)
    files: Dict[str, Optional[bytes]] = {
        readme_path: BRANCH_README.encode("utf-8"),
        index_path: runindex.serialize_index(merged, newer).encode("utf-8")}
    health = runindex.merge_lane_health(local_health, _parse_health(read_blob(repo, remote, health_path)))
    if runindex.LANE_HEALTH_FILE_NAME in legacy:
        health = runindex.merge_lane_health(health, _parse_health(legacy[runindex.LANE_HEALTH_FILE_NAME]))
    if health is not None:
        files[health_path] = runindex.serialize_lane_health(health).encode("utf-8")
    for name in legacy:
        files[name] = None
    return files, len(merged), len(remote_rows)


def publish(repo: str, remote: str, branch: str, rows: List[Dict], health: Optional[Dict],
            attempts: int = 4, delay: float = 2.0,
            before_push: Optional[Callable[[int], None]] = None, log=print,
            newer: Sequence["runindex.NewerLine"] = (), directory: str = DEFAULT_BRANCH_DIR) -> str:
    """Merge the local index (and lane health) with the branch's copy and push
    one commit on top of the remote tip. Returns "pushed", "created" or
    "unchanged". A rejected push is retried after a re-fetch and re-merge.
    Only the tool's own files are written (``_commit_files``); every other
    file on the branch is kept."""
    if not branch:
        raise RuntimeError("--branch is required")
    directory = branch_dir(directory)
    where = "%s/" % directory if directory else "the branch root"
    for attempt in range(1, attempts + 1):
        tip = remote_tip(repo, remote, branch)
        files, merged_count, remote_count = _merged_files(rows, health, tip, repo, newer, directory)
        if tip is not None and all(read_blob(repo, tip, n) == b for n, b in files.items()):
            log("run index: %s on %s already holds these %d rows; nothing to push"
                % (where, branch, merged_count))
            return "unchanged"
        moved = sorted(n for n, b in files.items() if b is None)
        message = "%s%d runs (%d from this machine, %d on the branch before)%s" % (
            COMMIT_SUBJECT_PREFIX, merged_count, len(rows), remote_count,
            "; moved %s from the branch root" % ", ".join(moved) if moved else "")
        commit = _commit_files(repo, tip, files, message, directory)
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


def fetch(repo: str, remote: str, branch: str, directory: str = DEFAULT_BRANCH_DIR
          ) -> Tuple[Optional[bytes], Optional[bytes], Optional[str]]:
    """(index, lane health, tip) off the branch: the files of ``directory``,
    or the root-layout files when the folder holds no index."""
    directory = branch_dir(directory)
    tip = remote_tip(repo, remote, branch)
    if tip is None:
        return None, None, None
    _readme, index_path, health_path = own_files(directory)
    index = read_blob(repo, tip, index_path)
    if index is None and directory:
        index_path, health_path = runindex.INDEX_FILE_NAME, runindex.LANE_HEALTH_FILE_NAME
        index = read_blob(repo, tip, index_path)
    return index, read_blob(repo, tip, health_path), tip


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
    if rep.get("lastSnapshot"):
        print("  last snapshot (not a PASS, verdict unknown; counted apart): %s  snapshots=%d"
              % (_fmt_brief(rep["lastSnapshot"]), rep["windows"]["all"].get("snapshots", 0)))
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
    snaps = s.get("snapshots") or {}
    if snaps.get("rows"):
        print("  snapshot rows (not PASS, verdict unknown; in no count above): %d over %d lane(s), "
              "%d lane(s) known only from snapshots: %s" % (
                  snaps["rows"], snaps["lanes"], len(snaps["lanesOnlyInSnapshots"]),
                  " ".join(snaps["lanesOnlyInSnapshots"]) or "-"))
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


def cmd_import_snapshots(args) -> int:
    logs_dir = os.path.abspath(args.from_dir)
    if not os.path.isdir(logs_dir):
        print("run_index: logs folder %s does not exist" % logs_dir, file=sys.stderr)
        return 1
    archive = _archive(args)
    if not args.dry_run:
        os.makedirs(archive, exist_ok=True)
    counts, decisions, facts = import_snapshots(logs_dir, archive, args.dry_run)
    imported = [rid for _n, outcome, _r, rid in decisions if outcome == "import"]
    print("import-snapshots%s: %d folder(s) in %s, %d imported%s into %s" % (
        " (dry run)" if args.dry_run else "", len(decisions), logs_dir, len(imported),
        " (would be)" if args.dry_run else "", archive))
    for (outcome, reason), n in sorted(counts.items()):
        print("  %-6s %-24s %d" % (outcome, reason, n))
    print("  imports: commit %d, dirty %d, dll %d, profile %d" % (
        facts["withCommit"], facts["dirty"], facts["withDll"], facts["withProfile"]))
    if facts["matchedVerdicts"]:
        print("  verdicts of the archived runs a snapshot matched (a PASS means the harness did collect "
              "logs for a passing run): %s" % ", ".join("%s %d" % kv for kv in facts["matchedVerdicts"].items()))
    if args.list:
        for name, outcome, reason, rid in decisions:
            print("    %s -> %s %s%s" % (name, outcome, reason, (" " + rid) if rid else ""))
    if imported and args.append and not args.dry_run:
        acounts, skipped, path = append_runs(archive, imported, _index_path(args))
        print("run index: %s -> %s" % (format_append_counts(acounts), path))
        _report_skipped(skipped)
    return 0


def cmd_lane(args) -> int:
    rows = _need_rows(_index_path(args))
    rep = runindex.lane_report(rows, args.lane, args.now)
    if not rep["lastRun"] and not rep["lastSnapshot"]:
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
                                                       p["error"], p["parsek"], p["bytes"], _short(p["commit"], 9))
              + (" (snapshot)" if p.get("origin") == runindex.SNAPSHOT_ORIGIN else ""))
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
    found, below = runindex.lanes_for_below(data, args.module, args.subsystem, args.min_lines)
    if args.json:
        _print_json(found)
        return 0
    what = ("module %s" % args.module) if args.module else ("subsystem %s" % args.subsystem)
    print("%d lane(s) log %s (latest PASS of each); %d more logged it under %d line(s) "
          "with no warning or error (--min-lines)" % (len(found), what, below, args.min_lines))
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
                args.attempts, args.delay, newer=parsed.newer, directory=args.dir)
    except RuntimeError as exc:
        print("run_index: %s" % exc, file=sys.stderr)
        return 1
    return 0


def cmd_fetch(args) -> int:
    try:
        index, health, tip = fetch(os.path.abspath(args.repo), args.remote, args.branch, args.dir)
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

    def dir_arg(p):
        p.add_argument("--dir", default=DEFAULT_BRANCH_DIR,
                       help="folder of the branch for the flight files (default %s; '' is the branch root, "
                            "the first layout)" % DEFAULT_BRANCH_DIR)

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

    p = sub.add_parser("import-snapshots",
                       help="import the harness collect-logs snapshot folders of a logs folder into the archive")
    index_arg(p)
    p.add_argument("--from", dest="from_dir", required=True,
                   help="the folder holding the snapshot folders (<umbrella>/logs)")
    p.add_argument("--dry-run", action="store_true", help="decide and count, write nothing")
    p.add_argument("--list", action="store_true", help="print each folder's decision")
    p.add_argument("--append", action="store_true", help="also append the imported runs to the index")
    p.set_defaults(func=cmd_import_snapshots)

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
    p.add_argument("--min-lines", type=int, default=runindex.LANES_FOR_MIN_LINES,
                   help="leave out a lane that logged fewer lines than this of the module or tag and no "
                        "warning or error of it (default %d: one boot or scene load writes a few one-shot "
                        "lines of most modules, so without a floor every lane matches; 0 lists all)"
                        % runindex.LANES_FOR_MIN_LINES)
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_lanes_for)

    p = sub.add_parser("publish", help="merge the index into a git branch and push (no checkout needed)")
    p.add_argument("--index", required=True)
    p.add_argument("--health", default=None, help="lane-health file to publish beside it")
    p.add_argument("--repo", required=True, help="any checkout or worktree of the repository")
    p.add_argument("--remote", required=True)
    p.add_argument("--branch", required=True,
                   help="the data branch (no default; the owner's ruling of 2026-11-12 names metrics)")
    dir_arg(p)
    p.add_argument("--attempts", type=int, default=4)
    p.add_argument("--delay", type=float, default=2.0)
    p.set_defaults(func=cmd_publish)

    p = sub.add_parser("fetch", help="read the index (and lane health) off a git branch")
    p.add_argument("--repo", required=True)
    p.add_argument("--remote", required=True)
    p.add_argument("--branch", required=True, help="the data branch (metrics, per the owner's ruling)")
    dir_arg(p)
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
