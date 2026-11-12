"""The harness run index and per-lane runtime health (pure).

run.py archives every run attempt to ``<umbrella>/automation/results-archive/<runId>/``
(HARNESS-RESULTS-ARCHIVE). That archive is only on the machine that flew the
runs. This module turns it into data any session can read: one compact JSON
line per archived attempt (``index.jsonl``), plus a smaller per-lane health
breakdown (``lane-health.json``). The thin shell is ``harness/tools/run_index.py``
(file reads, gzip, git); every decision is here and takes plain values.

Index row (schema ``parsek-run-index/1``; one line, sorted keys, no spaces;
rows ordered by ``started`` then ``runId``). Every key and where it comes from
(``<runId>.json`` is the result, ``manifest.json`` the archive manifest):

- ``schema``: ``parsek-run-index/1``.
- ``runId``: the archive folder name (``<stamp>_<scenarioId>[_run<N>][_a<N>]``).
- ``lane``: result ``scenarioId`` (else the run id's scenario part).
- ``tier``: result ``tier`` (null when the result has none).
- ``profile``: result ``instanceProfile``.
- ``started``: result ``startedUtc`` (``YYYY-MM-DDTHH:MM:SSZ``), else the run
  id's minute stamp (``...:00Z``) and a ``gaps`` entry.
- ``wall``: result ``wallSeconds`` (this attempt).
- ``attempt``: result ``attempt`` (else the run id's ``_a<N>``, else 1).
- ``final`` / ``byRetry``: derived over the whole index, never read: the
  attempt with the highest number among the rows sharing a run id stem (the
  run id without ``_a<N>``) is ``final``; ``byRetry`` (present only when true)
  marks a final row whose attempt is above 1 (the scenario's verdict was
  reached by a retry).
- ``verdict``: result ``verdict`` (the failure class: ``PARSEK-FAIL``,
  ``INVALID``, ``KILLED``...).
- ``commit``: manifest ``gitHead``, first ``COMMIT_HEX`` hex digits (the
  worktree HEAD when the run was archived; null for a back-filled run, whose
  HEAD then says nothing).
- ``dll``: manifest ``parsekDllSha256``, first ``DLL_HEX`` hex digits (the
  deployed DLL).
- ``worktree``: the manifest ``worktree`` path's last folder name only.
- ``origin``: manifest ``origin`` (``run`` or ``backfill``).
- ``health``: the compact KSP.log figures below, or null when the archive holds
  no readable ``KSP.log.gz``.

Present only when they hold something: ``missionWall`` (result
``missionWallSeconds``; absent on a seam-only run), ``subkind`` (result
``subkind``: what failed), ``note`` (result ``note``: ``flakedThenPassed`` on a
retried PASS), ``harnessLog`` (manifest ``harnessLog``: ``slice`` or ``full``),
``parsekFrames`` (result ``verifiers.unityExceptions.parsekFrames``: Unity
exceptions with a Parsek frame, when above 0), ``gaps`` (what could not be
read).

``health`` (from ``KSP.log.gz``, one streaming pass, ``LogScan``):
``bytes`` (uncompressed), ``lines`` (all lines), ``parsek`` (Parsek lines),
``verbose`` / ``info`` / ``warn`` / ``error`` (Parsek lines per level),
``unity`` (raw Unity exception lines: the counted patterns of
``hlib.UNITY_EXCEPTION_PATTERNS`` plus the frames-only openers, per line, Parsek
lines skipped, the same rule as ``hlib.scan_unity_exception_stacks``), ``top``
(the ``TOP_SUBSYSTEMS`` tags with the most lines, ``[tag, lines]``), and the
performance facts below, each present only when the run logged it.

Performance facts. Only what the mod logs today; each parser is pinned to the C#
literal by ``test_runindex.HealthFactSourceSyncTests`` (a reworded line reds
there instead of reading zero). All are on the ``Diagnostics`` tag:

- ``load`` ``[count, maxMs, sumMs]``: ``OnLoad: {0}ms ({1} recordings) ...``,
  Source/Parsek/ParsekScenario.Diagnostics.cs:218 (written at :241, Verbose).
- ``save`` ``[count, maxMs, sumMs]``: ``OnSave: {0}ms ({1} dirty of {2})``,
  ParsekScenario.Diagnostics.cs:261 (written at :259, Verbose).
- ``playback`` ``[lines, frames, maxMs, maxGhosts]``: ``Playback frame budget
  exceeded: {0}ms ({1} ghosts, warp: {2}x)``,
  Source/Parsek/Diagnostics/DiagnosticsComputation.cs:847 (WarnRateLimited, 30 s,
  at :845). ``frames`` adds each line's ``| suppressed=N`` suffix (ParsekLog.cs,
  the rate limiter), so it counts the over-budget frames up to the last line
  (frames suppressed after it are never written: a lower bound).
- ``recording`` ``[lines, frames, maxMs]``: ``Recording frame exceeded budget:
  {0}ms for vessel ...``, DiagnosticsComputation.cs:1125 (WarnRateLimited at :1123).
- ``scene`` ``[count, maxRecordings, maxGhosts]``: ``Scene load complete ({0}):
  {1} recordings, ... {6} ghosts``, DiagnosticsComputation.cs:1161 (Verbose at :1159).
- ``growth`` ``[count, maxPoints, sumEstBytes]``: ``Recording growth rate at stop:
  {0} points, ... est {5} bytes``, Source/Parsek/FlightRecorder.PointCommit.cs:279
  (Verbose, FlightRecorder.StopAndFinalize.cs:174).

NOT logged by the mod (so not measured here): frame time on frames under the
8 ms playback / 4 ms recording budget (``PlaybackBudgetThresholdMs`` /
``RecordingBudgetThresholdMs``, DiagnosticsComputation.cs:705 / :710; only
exceedances are logged), hence no frame-time percentiles against ghost count;
per-frame or per-scene managed allocations and GC counts. The on-demand report
(``RunDiagnosticsReport``, DiagnosticsComputation.cs:544, written by the
Settings button and the in-game ``Diagnostics`` tests) holds a 4 s rolling
playback avg / peak and a ``GC.CollectionCount(0)`` delta, but only for the
moment it is asked for and only in lanes that run it, so it is not parsed. The
one line each missing measurement would need is in ``NOT_LOGGED``.

Lane health (``lane-health.json``, schema ``parsek-lane-health/1``): per lane,
from its latest PASS row, Parsek lines / warnings / errors per subsystem tag and
per module (``[lines, warn, error]``, or just ``lines`` when both are 0). Tags map to modules through the committed ``scripts/arch/log_tags.json``
(generated from the compiled IL by ``scripts/arch/logtags.py``; this module reads
the file's data and never imports scripts/arch). A tag credits its lines to its
primary module (the one with the most call sites); a tag the map lacks lands in
``unmapped``, counted, never an error.

Everything here is pure: rows in, rows out; text in, data out.
"""

from __future__ import annotations

import json
import re
from collections import Counter, defaultdict
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

import hlib

SCHEMA = "parsek-run-index/1"
LANE_HEALTH_SCHEMA = "parsek-lane-health/1"
TAG_MAP_SCHEMA = "parsek-log-tags/1"
INDEX_FILE_NAME = "index.jsonl"
LANE_HEALTH_FILE_NAME = "lane-health.json"
TOP_SUBSYSTEMS = 3
LEVELS = ("VERBOSE", "INFO", "WARN", "ERROR")
LEVEL_KEYS = {"VERBOSE": "verbose", "INFO": "info", "WARN": "warn", "ERROR": "error"}
WINDOWS_DAYS = (30, 90)
REGRESSION_PRIOR_PASSES = 5
REGRESSION_MIN_PRIOR = 3
REGRESSION_GROWTH = 1.3
# Hash prefixes a row keeps: enough to name a commit / a DLL build, not more.
COMMIT_HEX = 12
DLL_HEX = 16

PASS = hlib.VERDICT_PASS
FAIL_VERDICTS = (hlib.VERDICT_PARSEK_FAIL, hlib.VERDICT_XPASS)
INVALID_VERDICTS = (hlib.VERDICT_INVALID, hlib.VERDICT_KILLED)

_ATTEMPT_RE = re.compile(r"_a(\d+)$")
_RUN_ID_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})_(\d{2})(\d{2})_(.+)$")
_ORDINAL_RE = re.compile(r"_run\d+$")


class IndexError_(ValueError):
    """An index line or row that cannot be read."""


# ---------------------------------------------------------------------------
# KSP.log scan (streaming; the shell feeds raw byte lines)
# ---------------------------------------------------------------------------

# `[Parsek][{level}][{safeSubsystem}] {safeMessage}` (ParsekLog.Write). KSP.log
# prefixes it with `[LOG hh:mm:ss.fff] ` (`[WRN ...]` / `[ERR ...]` for warnings
# and errors); the scan finds the token anywhere in the line.
PARSEK_TOKEN = b"[Parsek]["
PARSEK_LINE_RE = re.compile(rb"\[Parsek\]\[(VERBOSE|INFO|WARN|ERROR)\]\[([^\]\r\n]*)\] ?(.*?)\r?\n?$")
SUPPRESSED_RE = re.compile(r" \| suppressed=(\d+)$")
FACT_TAG = "Diagnostics"


class HealthFact:
    """One performance line the mod writes: where (C# file and the literal
    fragments the source holds verbatim), how it is parsed, and a sample of the
    rendered line with the values the parser must read back."""

    def __init__(self, key: str, regex: str, source: str, fragments: Tuple[str, ...],
                 sample_args: Dict[str, str], expected: Tuple[str, ...],
                 writer: Tuple[str, str]):
        self.key = key
        # (file, regex) of the ParsekLog call that writes the line on FACT_TAG.
        self.writer = (writer[0], re.compile(writer[1]))
        self.regex = re.compile(regex)
        self.source = source
        self.fragments = fragments
        self.sample_args = sample_args
        self.expected = expected


_SD = "Source/Parsek/ParsekScenario.Diagnostics.cs"
_DC = "Source/Parsek/Diagnostics/DiagnosticsComputation.cs"

HEALTH_FACTS: Tuple[HealthFact, ...] = (
    HealthFact("load", r"^OnLoad: (\d+)ms \((\d+) recordings\)",
               "Source/Parsek/ParsekScenario.Diagnostics.cs",
               ("OnLoad: {0}ms ({1} recordings) phase={2} status={3}",),
               {"0": "1234", "1": "7", "2": "cold", "3": "ok"}, ("1234", "7"),
               (_SD, r'ParsekLog\.Verbose\("Diagnostics",\s*FormatLoadTimingMessage\(')),
    HealthFact("save", r"^OnSave: (\d+)ms \((\d+) dirty of (\d+)\)",
               "Source/Parsek/ParsekScenario.Diagnostics.cs",
               ("OnSave: {0}ms ({1} dirty of {2})",),
               {"0": "88", "1": "2", "2": "9"}, ("88", "2", "9"),
               (_SD, r'ParsekLog\.Verbose\("Diagnostics",\s*string\.Format\(CultureInfo\.InvariantCulture,\s*"OnSave: ')),
    HealthFact("playback", r"^Playback frame budget exceeded: ([0-9.]+)ms \((\d+) ghosts, warp: ([0-9.]+)x\)",
               "Source/Parsek/Diagnostics/DiagnosticsComputation.cs",
               ("Playback frame budget exceeded: {0}ms ({1} ghosts, warp: {2}x)",),
               {"0": "21.4", "1": "12", "2": "1"}, ("21.4", "12", "1"),
               (_DC, r'ParsekLog\.WarnRateLimited\("Diagnostics", "playback-budget",\s*string\.Format\(Inv,\s*"Playback frame budget')),
    HealthFact("recording", r"^Recording frame exceeded budget: ([0-9.]+)ms for vessel ",
               "Source/Parsek/Diagnostics/DiagnosticsComputation.cs",
               ('Recording frame exceeded budget: {0}ms for vessel \\"{1}\\"',),
               {"0": "9.75", "1": "Kerbal X"}, ("9.75",),
               (_DC, r'ParsekLog\.WarnRateLimited\("Diagnostics", "recording-budget",\s*string\.Format\(Inv,\s*"Recording frame exceeded')),
    HealthFact("scene", r"^Scene load complete \((.*?)\): (\d+) recordings, .*, (\d+) ghosts$",
               "Source/Parsek/Diagnostics/DiagnosticsComputation.cs",
               ("Scene load complete ({0}): {1} recordings, ~{2} memory est ({3} pts, {4} evts, {5} segs), "
                "{6} ghosts",),
               {"0": "FLIGHT", "1": "4", "2": "1.2 MB", "3": "900", "4": "40", "5": "3", "6": "2"},
               ("FLIGHT", "4", "2"),
               (_DC, r'ParsekLog\.Verbose\("Diagnostics",\s*string\.Format\(Inv,\s*"Scene load complete ')),
    HealthFact("growth", r"^Recording growth rate at stop: (\d+) points, (\d+) events, .* est (\d+) bytes$",
               "Source/Parsek/FlightRecorder.PointCommit.cs",
               ("Recording growth rate at stop: {0} points, {1} events, {2:F1}s elapsed, ",
                "{3:F2} pts/s, {4:F2} evts/s, est {5} bytes"),
               {"0": "512", "1": "33", "2": "61.0", "3": "8.39", "4": "0.54", "5": "43520"},
               ("512", "33", "43520"),
               ("Source/Parsek/FlightRecorder.StopAndFinalize.cs",
                r'ParsekLog\.Verbose\("Diagnostics",\s*FormatGrowthRateAtStop\(')),
)
FACTS_BY_KEY = {f.key: f for f in HEALTH_FACTS}

# The one log line each missing measurement would need (report and docs only).
NOT_LOGGED: Tuple[Tuple[str, str], ...] = (
    ("frame-time percentiles against ghost count",
     "[Parsek][INFO][Diagnostics] Playback frame window: frames=<n> p50=<ms> p90=<ms> p99=<ms> "
     "maxMs=<ms> ghostsP50=<n> ghostsMax=<n> warpMax=<x> (one line per scene, or per 60 s of play)"),
    ("recording frame time on frames under budget",
     "[Parsek][INFO][Diagnostics] Recording frame window: frames=<n> p50=<ms> p90=<ms> maxMs=<ms> "
     "vessels=<n> (one line per recording stop)"),
    ("per-frame managed allocations and GC",
     "[Parsek][INFO][Diagnostics] Alloc window: frames=<n> gcGen0=<n> allocKbPerFrameP50=<kb> "
     "allocKbPerFrameMax=<kb> (one line per scene)"),
)

# hlib's raw-Unity-exception rule, applied per byte line. Every pattern holds
# "Exception" or is the `[EXC` record (pinned by a test), so the prefilter is exact.
_UNITY_COUNTED = tuple(re.compile(p.pattern.encode("ascii")) for _n, p in hlib.UNITY_EXCEPTION_PATTERNS)
_UNITY_EXC_RECORD = re.compile(hlib.UNITY_EXC_RECORD.pattern.encode("ascii"))
_UNITY_EVENT_HANDLER = re.compile(hlib.UNITY_EVENT_HANDLER_EXCEPTION.pattern.encode("ascii"))


def unity_exception_hits(line: bytes) -> int:
    """How many raw Unity exception occurrences one non-Parsek line holds:
    one per counted pattern it matches, else 1 for a frames-only opener."""
    if b"Exception" not in line and not line.startswith(b"[EXC"):
        return 0
    hits = sum(1 for pat in _UNITY_COUNTED if pat.search(line) is not None)
    if hits:
        return hits
    if _UNITY_EXC_RECORD.match(line) is not None or _UNITY_EVENT_HANDLER.search(line) is not None:
        return 1
    return 0


class LogScan:
    """Streaming KSP.log scan: ``feed`` each raw line (bytes, with its newline),
    then ``compact()`` / ``breakdown()``."""

    def __init__(self):
        self.bytes = 0
        self.lines = 0
        self.levels = Counter()
        # tag -> [lines, warn, error, bytes]
        self.subsystems: Dict[str, List[int]] = {}
        self.unity = 0
        self.facts: Dict[str, List[float]] = {}

    def feed(self, line: bytes) -> None:
        self.bytes += len(line)
        self.lines += 1
        at = line.find(PARSEK_TOKEN)
        if at < 0:
            if b"[Parsek]" not in line:
                self.unity += unity_exception_hits(line)
            return
        m = PARSEK_LINE_RE.match(line, at)
        if m is None:
            return
        level = m.group(1).decode("ascii")
        tag = m.group(2).decode("utf-8", "replace")
        self.levels[level] += 1
        row = self.subsystems.get(tag)
        if row is None:
            row = self.subsystems[tag] = [0, 0, 0, 0]
        row[0] += 1
        row[3] += len(line)
        if level == "WARN":
            row[1] += 1
        elif level == "ERROR":
            row[2] += 1
        if tag == FACT_TAG:
            self._fact(m.group(3).decode("utf-8", "replace"))

    def _fact(self, message: str) -> None:
        suppressed = 0
        s = SUPPRESSED_RE.search(message)
        if s is not None:
            suppressed = int(s.group(1))
            message = message[:s.start()]
        for fact in HEALTH_FACTS:
            m = fact.regex.match(message)
            if m is None:
                continue
            g = m.groups()
            acc = self.facts.get(fact.key)
            if fact.key in ("load", "save"):
                ms = int(g[0])
                if acc is None:
                    acc = self.facts[fact.key] = [0, 0, 0]
                acc[0] += 1
                acc[1] = max(acc[1], ms)
                acc[2] += ms
            elif fact.key == "playback":
                if acc is None:
                    acc = self.facts[fact.key] = [0, 0, 0.0, 0]
                acc[0] += 1
                acc[1] += 1 + suppressed
                acc[2] = max(acc[2], float(g[0]))
                acc[3] = max(acc[3], int(g[1]))
            elif fact.key == "recording":
                if acc is None:
                    acc = self.facts[fact.key] = [0, 0, 0.0]
                acc[0] += 1
                acc[1] += 1 + suppressed
                acc[2] = max(acc[2], float(g[0]))
            elif fact.key == "scene":
                if acc is None:
                    acc = self.facts[fact.key] = [0, 0, 0]
                acc[0] += 1
                acc[1] = max(acc[1], int(g[1]))
                acc[2] = max(acc[2], int(g[2]))
            elif fact.key == "growth":
                if acc is None:
                    acc = self.facts[fact.key] = [0, 0, 0]
                acc[0] += 1
                acc[1] = max(acc[1], int(g[0]))
                acc[2] += int(g[2])
            return

    def parsek_lines(self) -> int:
        return sum(self.levels.values())

    def compact(self) -> Dict[str, Any]:
        """The row's ``health`` value."""
        top = sorted(self.subsystems.items(), key=lambda kv: (-kv[1][0], kv[0]))[:TOP_SUBSYSTEMS]
        out: Dict[str, Any] = {
            "bytes": self.bytes,
            "lines": self.lines,
            "parsek": self.parsek_lines(),
            "unity": self.unity,
            "top": [[tag, row[0]] for tag, row in top],
        }
        for level in LEVELS:
            out[LEVEL_KEYS[level]] = self.levels.get(level, 0)
        for key, acc in sorted(self.facts.items()):
            out[key] = [round(v, 2) if isinstance(v, float) else v for v in acc]
        return out

    def breakdown(self) -> Dict[str, List[int]]:
        """Per tag ``[lines, warn, error]`` (the lane-health detail)."""
        return {tag: row[:3] for tag, row in sorted(self.subsystems.items())}


def scan_lines(lines: Iterable[bytes]) -> LogScan:
    scan = LogScan()
    for line in lines:
        scan.feed(line)
    return scan


# ---------------------------------------------------------------------------
# Rows
# ---------------------------------------------------------------------------

def attempt_of(run_id: str) -> int:
    m = _ATTEMPT_RE.search(run_id or "")
    return int(m.group(1)) if m else 1


def run_stem(run_id: str) -> str:
    """The run id without its ``_a<N>`` attempt suffix: one scenario run."""
    return _ATTEMPT_RE.sub("", run_id or "")


def lane_from_run_id(run_id: str) -> Optional[str]:
    m = _RUN_ID_RE.match(run_stem(run_id))
    if not m:
        return None
    return _ORDINAL_RE.sub("", m.group(4))


def started_from_run_id(run_id: str) -> Optional[str]:
    m = _RUN_ID_RE.match(run_id or "")
    if not m:
        return None
    return "%sT%s:%s:00Z" % (m.group(1), m.group(2), m.group(3))


def folder_name(path: Optional[str]) -> Optional[str]:
    """The last folder of a Windows or POSIX path (no machine path in a row)."""
    if not path:
        return None
    parts = [p for p in re.split(r"[\\/]+", str(path)) if p]
    return parts[-1] if parts else None


def _int_or_none(value) -> Optional[int]:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return int(value)
    return None


def _short_hex(value, n: int) -> Optional[str]:
    return value[:n] if isinstance(value, str) and value else None


def build_row(run_id: str, result: Optional[Dict], manifest: Optional[Dict],
              health: Optional[Dict], gaps: Sequence[str] = ()) -> Tuple[Optional[Dict], Optional[str]]:
    """(row, None) or (None, skip reason). ``result`` is the parsed result JSON,
    ``manifest`` the parsed manifest (None when absent), ``health`` the
    ``LogScan.compact()`` value (None when there is no readable KSP.log)."""
    if not isinstance(result, dict):
        return None, "no readable result json"
    verdict = result.get("verdict")
    if not isinstance(verdict, str) or not verdict:
        return None, "result json has no verdict"
    gap_list = list(gaps)
    rid = result.get("runId")
    if rid and rid != run_id:
        gap_list.append("result runId %s differs from the folder" % rid)
    lane = result.get("scenarioId") or lane_from_run_id(run_id)
    if not lane:
        return None, "no scenario id"
    started = result.get("startedUtc")
    if not isinstance(started, str) or not started:
        started = started_from_run_id(run_id)
        gap_list.append("no startedUtc")
    attempt = _int_or_none(result.get("attempt")) or attempt_of(run_id)
    m = manifest if isinstance(manifest, dict) else {}
    if not isinstance(manifest, dict):
        gap_list.append("no manifest")
    row: Dict[str, Any] = {
        "schema": SCHEMA,
        "runId": run_id,
        "lane": lane,
        "tier": result.get("tier"),
        "profile": result.get("instanceProfile"),
        "started": started,
        "wall": _int_or_none(result.get("wallSeconds")),
        "attempt": attempt,
        "verdict": verdict,
        "commit": _short_hex(m.get("gitHead"), COMMIT_HEX),
        "dll": _short_hex(m.get("parsekDllSha256"), DLL_HEX),
        "worktree": folder_name(m.get("worktree")),
        "origin": m.get("origin"),
        "health": health,
        "final": True,
    }
    if attempt > 1:
        row["byRetry"] = True
    # Optional keys ride only when they hold something (row size).
    optional = {
        "missionWall": _int_or_none(result.get("missionWallSeconds")),
        "subkind": result.get("subkind") or None,
        "note": result.get("note") or None,
        "harnessLog": m.get("harnessLog") or None,
    }
    for key, value in optional.items():
        if value is not None:
            row[key] = value
    ue = ((result.get("verifiers") or {}).get("unityExceptions") or {}) \
        if isinstance(result.get("verifiers"), dict) else {}
    frames = _int_or_none(ue.get("parsekFrames")) if isinstance(ue, dict) else None
    if frames:
        row["parsekFrames"] = frames
    if gap_list:
        row["gaps"] = sorted(set(gap_list))
    return row, None


def row_line(row: Dict) -> str:
    return json.dumps(row, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def sort_key(row: Dict) -> Tuple[str, str]:
    return (row.get("started") or "", row.get("runId") or "")


def normalize(rows: Iterable[Dict]) -> List[Dict]:
    """One row per run id (the last given wins), ``final`` / ``byRetry``
    derived over the set, ordered by start time then run id."""
    by_id: Dict[str, Dict] = {}
    for row in rows:
        by_id[row["runId"]] = dict(row)
    top: Dict[str, int] = {}
    for row in by_id.values():
        stem = run_stem(row["runId"])
        top[stem] = max(top.get(stem, 0), int(row.get("attempt") or 1))
    for row in by_id.values():
        attempt = int(row.get("attempt") or 1)
        final = attempt >= top[run_stem(row["runId"])]
        row["final"] = final
        if final and attempt > 1:
            row["byRetry"] = True
        else:
            row.pop("byRetry", None)
    return sorted(by_id.values(), key=sort_key)


def serialize_index(rows: Iterable[Dict]) -> str:
    rows = normalize(rows)
    return "".join(row_line(r) + "\n" for r in rows)


def parse_index(text: str) -> Tuple[List[Dict], List[str]]:
    """(rows, problems). A line that is not a JSON object of this schema with a
    run id is skipped and named, never fatal."""
    rows: List[Dict] = []
    problems: List[str] = []
    for number, line in enumerate((text or "").splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except ValueError as exc:
            problems.append("line %d is not JSON (%s)" % (number, exc))
            continue
        if not isinstance(row, dict) or row.get("schema") != SCHEMA or not row.get("runId"):
            problems.append("line %d is not a %s row" % (number, SCHEMA))
            continue
        rows.append(row)
    return rows, problems


def merge_rows(local: Iterable[Dict], other: Iterable[Dict]) -> List[Dict]:
    """Union by run id; the LOCAL row wins when both hold one. Normalized."""
    merged: Dict[str, Dict] = {}
    for row in other:
        merged[row["runId"]] = row
    for row in local:
        merged[row["runId"]] = row
    return normalize(merged.values())


def upsert(rows: Iterable[Dict], new_rows: Iterable[Dict]) -> Tuple[List[Dict], Dict[str, int]]:
    """Add or replace rows by run id. Returns (normalized rows, counts of
    added / replaced / unchanged). Appending a row already present byte for
    byte changes nothing (idempotent)."""
    current = {r["runId"]: r for r in normalize(rows)}
    counts = {"added": 0, "replaced": 0, "unchanged": 0}
    for row in new_rows:
        old = current.get(row["runId"])
        if old is None:
            counts["added"] += 1
        elif _content(old) == _content(row):
            counts["unchanged"] += 1
            continue
        else:
            counts["replaced"] += 1
        current[row["runId"]] = row
    return normalize(current.values()), counts


def _content(row: Dict) -> str:
    """A row without the two keys ``normalize`` derives over the whole index."""
    return row_line({k: v for k, v in row.items() if k not in ("final", "byRetry")})


# ---------------------------------------------------------------------------
# Queries
# ---------------------------------------------------------------------------

def _epoch_day(iso: Optional[str]) -> Optional[float]:
    """Days since 1970 of a ``YYYY-MM-DDTHH:MM:SSZ`` stamp (pure; no tz database)."""
    if not iso or len(iso) < 10:
        return None
    try:
        y, mo, d = int(iso[0:4]), int(iso[5:7]), int(iso[8:10])
        hh = int(iso[11:13]) if len(iso) >= 13 else 0
        mm = int(iso[14:16]) if len(iso) >= 16 else 0
    except ValueError:
        return None
    # Days from civil (Howard Hinnant's algorithm).
    y2 = y - (1 if mo <= 2 else 0)
    era = (y2 if y2 >= 0 else y2 - 399) // 400
    yoe = y2 - era * 400
    doy = (153 * (mo + (-3 if mo > 2 else 9)) + 2) // 5 + d - 1
    doe = yoe * 365 + yoe // 4 - yoe // 100 + doy
    return era * 146097 + doe - 719468 + (hh * 60 + mm) / 1440.0


def percentile(values: Sequence[float], q: float) -> Optional[float]:
    """Nearest-rank percentile (q in 0..100); None for no values."""
    vals = sorted(v for v in values if v is not None)
    if not vals:
        return None
    rank = max(1, int(-(-q * len(vals) // 100)))
    return vals[min(rank, len(vals)) - 1]


def median(values: Sequence[float]) -> Optional[float]:
    vals = sorted(v for v in values if v is not None)
    if not vals:
        return None
    mid = len(vals) // 2
    return vals[mid] if len(vals) % 2 else (vals[mid - 1] + vals[mid]) / 2.0


def index_now(rows: Sequence[Dict]) -> Optional[str]:
    """The newest start time in the index: the default end of every window, so
    the same index always answers the same way."""
    stamps = [r.get("started") for r in rows if r.get("started")]
    return max(stamps) if stamps else None


def lane_rows(rows: Iterable[Dict], lane: str) -> List[Dict]:
    return sorted((r for r in rows if r.get("lane") == lane), key=sort_key)


def _brief(row: Optional[Dict]) -> Optional[Dict]:
    if row is None:
        return None
    return {"runId": row.get("runId"), "started": row.get("started"), "verdict": row.get("verdict"),
            "subkind": row.get("subkind"), "commit": row.get("commit"), "dll": row.get("dll"),
            "wall": row.get("wall")}


def _counts(rows: Sequence[Dict]) -> Dict[str, Any]:
    verdicts = Counter(r.get("verdict") for r in rows)
    groups: Dict[str, List[Dict]] = defaultdict(list)
    for r in rows:
        groups[run_stem(r["runId"])].append(r)
    scenario_runs = 0
    flaked = 0
    final_pass = 0
    for attempts in groups.values():
        attempts.sort(key=lambda r: int(r.get("attempt") or 1))
        scenario_runs += 1
        last = attempts[-1]
        if last.get("verdict") == PASS:
            final_pass += 1
            if any(a.get("verdict") != PASS for a in attempts[:-1]):
                flaked += 1
    return {
        "attempts": len(rows),
        "passes": verdicts.get(PASS, 0),
        "fails": sum(verdicts.get(v, 0) for v in FAIL_VERDICTS),
        "invalids": sum(verdicts.get(v, 0) for v in INVALID_VERDICTS),
        "byVerdict": dict(sorted((k or "?", v) for k, v in verdicts.items())),
        "scenarioRuns": scenario_runs,
        "finalPasses": final_pass,
        "flaked": flaked,
        "flakeRate": round(flaked / scenario_runs, 3) if scenario_runs else None,
    }


def _window(rows: Sequence[Dict], now: Optional[str], days: int) -> List[Dict]:
    end = _epoch_day(now)
    if end is None:
        return list(rows)
    out = []
    for r in rows:
        t = _epoch_day(r.get("started"))
        if t is not None and end - t <= days:
            out.append(r)
    return out


def lane_report(rows: Sequence[Dict], lane: str, now: Optional[str] = None) -> Dict[str, Any]:
    """Everything ``run_index.py lane`` prints, as data."""
    now = now or index_now(rows)
    mine = lane_rows(rows, lane)
    passes = [r for r in mine if r.get("verdict") == PASS]
    walls = [r.get("wall") for r in mine if r.get("wall") is not None]
    windows = {"%dd" % d: _counts(_window(mine, now, d)) for d in WINDOWS_DAYS}
    windows["all"] = _counts(mine)
    return {
        "lane": lane,
        "tier": mine[-1].get("tier") if mine else None,
        "now": now,
        "lastRun": _brief(mine[-1] if mine else None),
        "lastPass": _brief(passes[-1] if passes else None),
        "windows": windows,
        "wallP50": percentile(walls, 50),
        "wallP90": percentile(walls, 90),
        "lastWalls": [[r.get("runId"), r.get("verdict"), r.get("wall")] for r in mine[-10:]],
    }


def lanes_of(rows: Iterable[Dict]) -> List[str]:
    return sorted({r.get("lane") for r in rows if r.get("lane")})


def summary(rows: Sequence[Dict], spec_tiers: Optional[Dict[str, Optional[str]]] = None,
            top: int = 20) -> Dict[str, Any]:
    """Totals per verdict and tier, lanes never passed, lanes whose last final
    run is not a PASS, the flakiest and slowest lanes, specs with no run."""
    by_lane: Dict[str, List[Dict]] = defaultdict(list)
    for r in rows:
        by_lane[r.get("lane")].append(r)
    per_tier: Dict[str, Counter] = defaultdict(Counter)
    for r in rows:
        per_tier[r.get("tier") or "?"][r.get("verdict") or "?"] += 1
    never_passed = []
    last_not_pass = []
    flaky = []
    slow = []
    for lane in sorted(by_lane):
        mine = sorted(by_lane[lane], key=sort_key)
        if not any(r.get("verdict") == PASS for r in mine):
            never_passed.append({"lane": lane, "attempts": len(mine),
                                 "lastVerdict": mine[-1].get("verdict")})
        finals = [r for r in mine if r.get("final")]
        last = finals[-1] if finals else mine[-1]
        if last.get("verdict") != PASS:
            last_not_pass.append({"lane": lane, "runId": last.get("runId"), "verdict": last.get("verdict"),
                                  "subkind": last.get("subkind")})
        c = _counts(mine)
        if c["flaked"]:
            flaky.append({"lane": lane, "flaked": c["flaked"], "scenarioRuns": c["scenarioRuns"],
                          "flakeRate": c["flakeRate"]})
        walls = [r.get("wall") for r in mine if r.get("wall") is not None]
        if walls:
            slow.append({"lane": lane, "wallP50": percentile(walls, 50), "wallP90": percentile(walls, 90),
                         "attempts": len(walls)})
    flaky.sort(key=lambda x: (-x["flaked"], -(x["flakeRate"] or 0), x["lane"]))
    slow.sort(key=lambda x: (-(x["wallP50"] or 0), x["lane"]))
    out: Dict[str, Any] = {
        "now": index_now(rows),
        "attempts": len(rows),
        "lanes": len(by_lane),
        "byVerdict": dict(sorted(Counter(r.get("verdict") or "?" for r in rows).items())),
        "byTier": {t: dict(sorted(c.items())) for t, c in sorted(per_tier.items())},
        "neverPassed": never_passed,
        "lastNotPass": last_not_pass,
        "flakiest": flaky[:top],
        "slowest": slow[:top],
    }
    if spec_tiers is not None:
        out["specsWithNoRun"] = sorted(l for l in spec_tiers if l not in by_lane)
        out["lanesWithNoSpec"] = sorted(l for l in by_lane if l not in spec_tiers)
    return out


def _md(text: Any) -> str:
    return str(text if text is not None else "").replace("|", "\\|")


def lane_table(rows: Sequence[Dict], spec_tiers: Optional[Dict[str, Optional[str]]] = None) -> str:
    """A Markdown table, one row per lane in the index (plus specs with no run
    when ``spec_tiers`` is given). Runs and pass rate count scenario runs (the
    final attempt decides); p50 wall is over every attempt."""
    lanes = set(lanes_of(rows))
    if spec_tiers is not None:
        lanes |= set(spec_tiers)
    lines = ["| Lane | Tier | Last verdict | Last pass (date, commit) | Runs | Pass rate | p50 wall s |",
             "|---|---|---|---|---|---|---|"]
    for lane in sorted(lanes):
        mine = lane_rows(rows, lane)
        tier = (mine[-1].get("tier") if mine else None) or (spec_tiers or {}).get(lane) or ""
        if not mine:
            lines.append("| %s | %s | (no run) | | 0 | | |" % (_md(lane), _md(tier)))
            continue
        finals = [r for r in mine if r.get("final")] or mine
        last = finals[-1]
        passes = [r for r in mine if r.get("verdict") == PASS]
        lp = passes[-1] if passes else None
        lp_text = ("%s, %s" % ((lp.get("started") or "")[:10], (lp.get("commit") or "?")[:9])) if lp else "never"
        c = _counts(mine)
        rate = "%d%%" % round(100.0 * c["finalPasses"] / c["scenarioRuns"]) if c["scenarioRuns"] else ""
        p50 = percentile([r.get("wall") for r in mine if r.get("wall") is not None], 50)
        verdict = last.get("verdict") or "?"
        if last.get("subkind"):
            verdict += "(%s)" % last["subkind"]
        lines.append("| %s | %s | %s | %s | %d | %s | %s |" % (
            _md(lane), _md(tier), _md(verdict), _md(lp_text), c["scenarioRuns"], rate,
            "" if p50 is None else p50))
    return "\n".join(lines) + "\n"


def trend(rows: Sequence[Dict], lane: str, n: int = 10) -> List[Dict[str, Any]]:
    """The lane's last ``n`` attempts: verdict, wall, warnings, errors, Parsek
    lines, log bytes and the commit, oldest first."""
    out = []
    for r in lane_rows(rows, lane)[-n:]:
        h = r.get("health") or {}
        out.append({"runId": r.get("runId"), "started": r.get("started"), "verdict": r.get("verdict"),
                    "wall": r.get("wall"), "warn": h.get("warn"), "error": h.get("error"),
                    "parsek": h.get("parsek"), "bytes": h.get("bytes"), "unity": h.get("unity"),
                    "commit": r.get("commit")})
    return out


def health_regressions(rows: Sequence[Dict], prior: int = REGRESSION_PRIOR_PASSES,
                       min_prior: int = REGRESSION_MIN_PRIOR,
                       growth: float = REGRESSION_GROWTH) -> List[Dict[str, Any]]:
    """Lanes whose latest PASS has more warnings or errors than the median of
    its previous ``prior`` PASS runs, or at least ``growth`` times their median
    wall time or log volume (log bytes or Parsek lines). Needs ``min_prior``
    earlier PASS runs with health. Report-only."""
    out = []
    for lane in lanes_of(rows):
        passes = [r for r in lane_rows(rows, lane) if r.get("verdict") == PASS and r.get("health")]
        if len(passes) < min_prior + 1:
            continue
        latest = passes[-1]
        before = passes[-1 - prior:-1]
        h = latest["health"]
        reasons = []
        for key in ("warn", "error"):
            med = median([p["health"].get(key) for p in before])
            if med is not None and h.get(key) is not None and h[key] > med:
                reasons.append("%s %s > median %s" % (key, h[key], _num(med)))
        med_wall = median([p.get("wall") for p in before])
        if med_wall and latest.get("wall") is not None and latest["wall"] >= growth * med_wall:
            reasons.append("wall %ss >= %.1fx median %ss" % (latest["wall"], growth, _num(med_wall)))
        for key in ("bytes", "parsek"):
            med = median([p["health"].get(key) for p in before])
            if med and h.get(key) is not None and h[key] >= growth * med:
                reasons.append("%s %s >= %.1fx median %s" % (key, h[key], growth, _num(med)))
        if reasons:
            out.append({"lane": lane, "runId": latest.get("runId"), "commit": latest.get("commit"),
                        "priorRuns": [p.get("runId") for p in before], "reasons": reasons})
    return out


def _num(v: float) -> str:
    return ("%d" % v) if float(v).is_integer() else ("%.1f" % v)


# ---------------------------------------------------------------------------
# Lane health (the per-subsystem / per-module breakdown)
# ---------------------------------------------------------------------------

def tag_modules(tag_map: Optional[Dict]) -> Dict[str, Tuple[str, Tuple[str, ...]]]:
    """{tag: (primary module, every module)} from the committed tag map's data.
    The primary module has the most call sites (ties by name). An unreadable or
    foreign map gives {} (every tag then reads unmapped)."""
    if not isinstance(tag_map, dict) or tag_map.get("schema") != TAG_MAP_SCHEMA:
        return {}
    out = {}
    for tag, entry in (tag_map.get("tags") or {}).items():
        mods = (entry or {}).get("modules") or {}
        if not mods:
            continue
        ranked = sorted(mods.items(), key=lambda kv: (-kv[1], kv[0]))
        out[tag] = (ranked[0][0], tuple(sorted(mods)))
    return out


def latest_pass_rows(rows: Iterable[Dict]) -> Dict[str, Dict]:
    """{lane: its latest PASS row}."""
    out: Dict[str, Dict] = {}
    for r in sorted(rows, key=sort_key):
        if r.get("verdict") == PASS:
            out[r["lane"]] = r
    return out


def _packed(counts: Sequence[int]):
    """``[lines, warn, error]``, or just ``lines`` when both are 0 (file size)."""
    lines, warn, error = (int(c) for c in counts[:3])
    return lines if not (warn or error) else [lines, warn, error]


def triple(value) -> List[int]:
    """A lane-health count (packed or not) as ``[lines, warn, error]``."""
    if isinstance(value, list):
        return [int(v) for v in value[:3]]
    return [int(value or 0), 0, 0]


def lane_health_entry(row: Dict, breakdown: Dict[str, Sequence[int]],
                      modules: Dict[str, Tuple[str, Tuple[str, ...]]]) -> Dict[str, Any]:
    """One lane's breakdown: per tag and per (primary) module ``[lines, warn,
    error]``, the other modules its shared tags also map to, and the unmapped
    tags."""
    by_module: Dict[str, List[int]] = {}
    shared: Dict[str, int] = {}
    unmapped: Dict[str, int] = {}
    for tag, counts in sorted(breakdown.items()):
        hit = modules.get(tag)
        if hit is None:
            unmapped[tag] = int(counts[0])
            continue
        primary, every = hit
        acc = by_module.setdefault(primary, [0, 0, 0])
        for i in range(3):
            acc[i] += int(counts[i])
        for other in every:
            if other != primary:
                shared[other] = shared.get(other, 0) + int(counts[0])
    entry = {
        "runId": row.get("runId"),
        "started": row.get("started"),
        "commit": row.get("commit"),
        "subsystems": {tag: _packed(counts) for tag, counts in sorted(breakdown.items())},
        "modules": {mod: _packed(counts) for mod, counts in sorted(by_module.items())},
        "unmapped": dict(sorted(unmapped.items())),
    }
    if shared:
        entry["sharedTagModules"] = dict(sorted(shared.items()))
    return entry


def build_lane_health(entries: Dict[str, Dict], tag_map: Optional[Dict]) -> Dict[str, Any]:
    """The ``lane-health.json`` document over per-lane entries."""
    unmapped_lanes: Counter = Counter()
    unmapped_lines: Counter = Counter()
    for entry in entries.values():
        for tag, lines in (entry.get("unmapped") or {}).items():
            unmapped_lanes[tag] += 1
            unmapped_lines[tag] += lines
    totals = (tag_map or {}).get("totals") if isinstance(tag_map, dict) else None
    return {
        "schema": LANE_HEALTH_SCHEMA,
        "tagMap": {"schema": (tag_map or {}).get("schema") if isinstance(tag_map, dict) else None,
                   "tags": (totals or {}).get("tags")},
        "lanes": {lane: entries[lane] for lane in sorted(entries)},
        "unmappedTags": {tag: [unmapped_lanes[tag], unmapped_lines[tag]] for tag in sorted(unmapped_lanes)},
    }


def serialize_lane_health(doc: Dict) -> str:
    return json.dumps(doc, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n"


def merge_lane_health(local: Optional[Dict], other: Optional[Dict]) -> Optional[Dict]:
    """Union by lane; the newer source run (start time, then run id) wins, the
    LOCAL entry on a tie. The tag-map summary is the local one when present."""
    docs = [d for d in (other, local) if isinstance(d, dict) and d.get("schema") == LANE_HEALTH_SCHEMA]
    if not docs:
        return None
    lanes: Dict[str, Dict] = {}
    for doc in docs:  # other first, so local wins a tie
        for lane, entry in (doc.get("lanes") or {}).items():
            old = lanes.get(lane)
            if old is None or sort_key(entry) >= sort_key(old):
                lanes[lane] = entry
    tag_info = (local or {}).get("tagMap") if isinstance(local, dict) else None
    doc = build_lane_health(lanes, None)
    doc["tagMap"] = tag_info or docs[0].get("tagMap")
    return doc


def lanes_for(doc: Dict, module: Optional[str] = None, subsystem: Optional[str] = None) -> List[Dict[str, Any]]:
    """Lanes whose latest PASS logged ``module`` (primary attribution, plus
    lanes reaching it only through a shared tag, marked) or ``subsystem``,
    most lines first."""
    out = []
    for lane, entry in (doc.get("lanes") or {}).items():
        if subsystem is not None:
            counts = (entry.get("subsystems") or {}).get(subsystem)
            if counts:
                counts = triple(counts)
                out.append({"lane": lane, "lines": counts[0], "warn": counts[1], "error": counts[2],
                            "runId": entry.get("runId")})
            continue
        counts = (entry.get("modules") or {}).get(module)
        if counts:
            counts = triple(counts)
            out.append({"lane": lane, "lines": counts[0], "warn": counts[1], "error": counts[2],
                        "runId": entry.get("runId")})
            continue
        shared = (entry.get("sharedTagModules") or {}).get(module)
        if shared:
            out.append({"lane": lane, "lines": shared, "warn": None, "error": None,
                        "runId": entry.get("runId"), "sharedTagOnly": True})
    out.sort(key=lambda x: (bool(x.get("sharedTagOnly")), -x["lines"], x["lane"]))
    return out


def lane_top_modules(doc: Dict, lane: str) -> List[Tuple[str, List[int]]]:
    """``lane``'s modules by Parsek lines, most first, as ``[lines, warn, error]``."""
    entry = (doc.get("lanes") or {}).get(lane) or {}
    mods = [(m, triple(v)) for m, v in (entry.get("modules") or {}).items()]
    return sorted(mods, key=lambda kv: (-kv[1][0], kv[0]))
