#!/usr/bin/env python3
"""V3 contact sheets: numbers next to pictures (design-testing-unified section 6).

Per run, emits ``results/<runId>_contact.html`` -- any captured screenshots as an
image grid (file references only, NO image decoding) side by side with the run's
key log lines extracted from the always-collected ``results/<runId>_shots/KSP.log``:

  - every ``BATCH_COMPLETE v1`` line (the in-game batch tally contract),
  - every ``faithful-parity summary`` line (the render-parity lens),
  - every ``phase=Anomaly`` raise, each with the +/-N nearest ``probe frame
    summary`` lines around it (the per-frame truth the anomaly fired against),
  - the verifier verdict rows from ``results/<runId>.json``.

Also emits ``results/index.html`` listing every run newest-first with a
verdict-colored badge, so "which run do I look at" is one glance.

The design goal (section 6 V3): a human scans "does this look right" and reads
the oracle verdict in the same glance, turning release validation into a
30-second scan instead of a play session. Until the V4 capture verbs land most
runs have no screenshots; the key-log panel is then the sheet's sole content,
which is still the whole point -- a green run finally leaves something a human
can look at.

CONTRACTS (binding):
  - Verdict-neutral and failure-isolated: this tool only READS run artifacts and
    only WRITES ``*_contact.html`` / ``index.html``. It never touches a result
    JSON, a verifier, or a classification, and every generate function is safe
    on partial/missing inputs (no KSP.log, no images, absent or malformed result
    JSON) -- a contact sheet must never crash the run flow.
  - Self-contained static HTML: no external assets, no scripts; inline CSS only.
    Readable in a browser and survivable in a quick terminal open (real
    newlines, log lines in <pre> blocks).
  - All log/JSON text is HTML-escaped before it touches the page (KSP.log is
    hostile input: vessel names are user-authored).

Usage::

    python harness/tools/contact_sheet.py                 # all sheets + index
    python harness/tools/contact_sheet.py --run-id <id>   # one sheet + index
    python harness/tools/contact_sheet.py --results-dir <dir> [--index-only]

Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import bisect
import html
import json
import math
import os
import sys
import urllib.parse
from typing import Dict, List, Optional, Sequence, Tuple

# ---------------------------------------------------------------------------
# Extraction contract (the consumed log-line shapes; see .claude/CLAUDE.md and
# hlib's anomaly anchoring). Substring markers, deliberately not regexes: the
# sheet is a viewer, not a gate -- it must show a slightly-drifted line rather
# than silently drop it.
# ---------------------------------------------------------------------------

BATCH_MARKER = "BATCH_COMPLETE v1"
PARITY_MARKER = "faithful-parity summary"
ANOMALY_MARKER = "phase=Anomaly"
PROBE_MARKER = "probe frame summary"

# +/-N probe-frame-summary lines shown around each anomaly raise (the design's
# "±N surrounding frames"): the probe emits one summary per sampled frame, so
# the nearest N before/after ARE the surrounding frames' truth.
ANOMALY_CONTEXT_PROBES = 3

# Per-section caps so a pathological log (an anomaly storm, a tracer left on
# for hours) cannot produce a 100 MB page. Every cap records how much it
# dropped -- silent truncation reads as "showed everything" when it didn't.
MAX_BATCH_LINES = 50
MAX_PARITY_LINES = 400
MAX_ANOMALY_BLOCKS = 200
MAX_LINE_CHARS = 500

IMAGE_EXTENSIONS = (".png", ".jpg", ".jpeg")
SHOTS_SUFFIX = "_shots"
CONTACT_SUFFIX = "_contact.html"
INDEX_BASENAME = "index.html"

# Verdict badge colors (index + sheet header). Unknown verdicts get gray.
VERDICT_COLORS = {
    "PASS": "#2e7d32",
    "EXPECTED-FAIL": "#00695c",
    "XPASS": "#6a1b9a",
    "PARSEK-FAIL": "#c62828",
    "INVALID": "#ef6c00",
    "KILLED": "#8e0000",
    "UNREADABLE": "#616161",
}
DEFAULT_VERDICT_COLOR = "#616161"

STATUS_COLORS = {
    "PASS": "#2e7d32",
    "FAIL": "#c62828",
    "INVALID": "#ef6c00",
    "SKIPPED": "#757575",
    "REPORT": "#1565c0",
}


# ---------------------------------------------------------------------------
# Pure extraction.
# ---------------------------------------------------------------------------


# A clipped line keeps its TAIL too (adversarial review NIT 8): the trailing
# tokens of a long line are often the payload (a BATCH_COMPLETE tally, a
# parity summary's skip.* counters), so a head-only clip would show a marker
# line with its numbers cut off.
CLIP_TAIL_CHARS = 80


def _clip(line: str, max_chars: int = MAX_LINE_CHARS) -> str:
    if len(line) <= max_chars:
        return line
    tail_keep = min(CLIP_TAIL_CHARS, max_chars // 2)
    head_keep = max(0, max_chars - tail_keep)
    dropped = len(line) - head_keep - tail_keep
    # line[-0:] is the WHOLE string (review NEW-3), so a zero tail budget must
    # yield "" -- this is the one function whose entire job is bounding output.
    tail = line[-tail_keep:] if tail_keep > 0 else ""
    return "%s ...[clipped %d chars]... %s" % (line[:head_keep], dropped, tail)


def extract_key_log_lines(log_text: Optional[str]) -> Dict:
    """Extract the contact sheet's key lines from a KSP.log body.

    Returns::

        {"batchComplete": [line, ...],
         "parity": [line, ...],
         "anomalies": [{"line": ..., "before": [...], "after": [...]}, ...],
         "dropped": {"batchComplete": n, "parity": n, "anomalies": n},
         "totalLines": n}

    Anomaly context is the nearest ``ANOMALY_CONTEXT_PROBES`` probe-frame-summary
    lines before/after the raise IN LOG ORDER (the probe emits one summary per
    sampled frame). Safe on None/empty/garbage input: everything degrades to
    empty lists, never an exception.
    """
    out: Dict = {"batchComplete": [], "parity": [], "anomalies": [],
                 "dropped": {"batchComplete": 0, "parity": 0, "anomalies": 0},
                 "totalLines": 0}
    if not log_text:
        return out
    lines = log_text.splitlines()
    out["totalLines"] = len(lines)
    probe_indices: List[int] = []
    anomaly_indices: List[int] = []
    for i, line in enumerate(lines):
        if PROBE_MARKER in line:
            probe_indices.append(i)
        if BATCH_MARKER in line:
            if len(out["batchComplete"]) < MAX_BATCH_LINES:
                out["batchComplete"].append(_clip(line.strip()))
            else:
                out["dropped"]["batchComplete"] += 1
        if PARITY_MARKER in line:
            if len(out["parity"]) < MAX_PARITY_LINES:
                out["parity"].append(_clip(line.strip()))
            else:
                out["dropped"]["parity"] += 1
        if ANOMALY_MARKER in line:
            anomaly_indices.append(i)

    for i in anomaly_indices:
        if len(out["anomalies"]) >= MAX_ANOMALY_BLOCKS:
            out["dropped"]["anomalies"] += 1
            continue
        pos = bisect.bisect_left(probe_indices, i)
        before = probe_indices[max(0, pos - ANOMALY_CONTEXT_PROBES):pos]
        # A probe line AT the anomaly's own index is impossible (one line is one
        # marker site in practice), but bisect_right keeps it out of `after`
        # regardless.
        pos_after = bisect.bisect_right(probe_indices, i)
        after = probe_indices[pos_after:pos_after + ANOMALY_CONTEXT_PROBES]
        out["anomalies"].append({
            "line": _clip(lines[i].strip()),
            "before": [_clip(lines[j].strip()) for j in before],
            "after": [_clip(lines[j].strip()) for j in after],
        })
    return out


def verifier_rows(result_obj) -> List[Dict]:
    """The verifier verdict rows from a run's result JSON, viewer-shaped.

    Each row: ``{"name", "status", "note"}``. Tolerates every malformed shape
    (non-dict result, non-dict verifiers, non-dict rows, list rows like
    ``subprocessRetry``) by degrading the row, never raising.
    """
    rows: List[Dict] = []
    if not isinstance(result_obj, dict):
        return rows
    verifiers = result_obj.get("verifiers")
    if not isinstance(verifiers, dict):
        return rows
    for name in sorted(verifiers):
        row = verifiers[name]
        if isinstance(row, dict):
            status = str(row.get("status", "-"))
            extras = []
            for k in sorted(row):
                if k == "status":
                    continue
                v = row[k]
                if isinstance(v, (str, int, float, bool)) or v is None:
                    extras.append("%s=%s" % (k, v))
                elif isinstance(v, (list, dict)) and v:
                    extras.append("%s=%s" % (k, _clip(json.dumps(v, sort_keys=True), 160)))
            note = "  ".join(extras)
        elif isinstance(row, list):
            status = "-"
            note = "%d entr%s" % (len(row), "y" if len(row) == 1 else "ies")
        else:
            status = "-"
            note = str(row)
        rows.append({"name": name, "status": status, "note": _clip(note, 400)})
    return rows


def run_index_entry(result_obj, fallback_run_id: str) -> Dict:
    """One index row from a parsed result JSON (or garbage). Never raises."""
    if not isinstance(result_obj, dict):
        return {"runId": fallback_run_id, "scenarioId": "?", "verdict": "UNREADABLE",
                "subkind": "", "utc": "", "wallSeconds": None, "note": "unparseable result JSON"}
    # wallSeconds admits only a usable non-bool number (adversarial review
    # MINOR 2): python's json.load accepts bare Infinity/NaN, and int(inf)
    # raises -- one poison result JSON would then permanently break every
    # subsequent index render, the exact "never a crash" contract violation.
    # Ints are checked by TYPE only (review NEW-1): math.isfinite coerces to a
    # C double first, so a big int (10**400) would raise OverflowError inside
    # the very guard meant to prevent the crash; a Python int is always finite.
    wall = result_obj.get("wallSeconds")
    if isinstance(wall, bool):
        wall = None
    elif isinstance(wall, float) and not math.isfinite(wall):
        wall = None
    elif not isinstance(wall, (int, float)):
        wall = None
    return {
        "runId": str(result_obj.get("runId") or fallback_run_id),
        "scenarioId": str(result_obj.get("scenarioId") or "?"),
        "verdict": str(result_obj.get("verdict") or "?"),
        "subkind": str(result_obj.get("subkind") or ""),
        "utc": str(result_obj.get("endedUtc") or result_obj.get("startedUtc") or ""),
        "wallSeconds": wall,
        "note": str(result_obj.get("note") or ""),
    }


def sort_entries_newest_first(entries: Sequence[Dict]) -> List[Dict]:
    """Newest-first: UTC ISO strings compare lexically; runId (which starts with
    a UTC timestamp) breaks ties and carries entries with no timestamp."""
    return sorted(entries, key=lambda e: (str(e.get("utc") or ""),
                                          str(e.get("runId") or "")), reverse=True)


# ---------------------------------------------------------------------------
# Pure rendering. All dynamic text goes through html.escape; every href/src
# additionally goes through urllib.parse.quote so a screenshot name with spaces
# or '#' still resolves.
# ---------------------------------------------------------------------------

_PAGE_CSS = """
body { font-family: -apple-system, 'Segoe UI', Roboto, Arial, sans-serif;
       margin: 1.2em; background: #fafafa; color: #212121; }
a { color: #1565c0; }
h1 { font-size: 1.3em; margin: 0 0 0.3em 0; }
h2 { font-size: 1.05em; margin: 1.2em 0 0.4em 0; }
.badge { display: inline-block; padding: 2px 10px; border-radius: 4px;
         color: #fff; font-weight: bold; }
.meta { color: #555; margin: 0.2em 0 1em 0; }
table { border-collapse: collapse; margin: 0.4em 0; }
th, td { border: 1px solid #ddd; padding: 4px 8px; text-align: left;
         vertical-align: top; font-size: 0.92em; }
th { background: #eee; }
pre { background: #212121; color: #e0e0e0; padding: 8px; border-radius: 4px;
      overflow-x: auto; font-size: 0.82em; line-height: 1.35;
      white-space: pre-wrap; word-break: break-all; }
pre .anomaly { color: #ff8a80; font-weight: bold; }
.columns { display: flex; flex-wrap: wrap; gap: 1.2em; align-items: flex-start; }
.shots { flex: 1 1 380px; min-width: 300px; }
.keylines { flex: 2 1 520px; min-width: 320px; }
.grid { display: flex; flex-wrap: wrap; gap: 10px; }
figure { margin: 0; }
figure img { max-width: 360px; max-height: 240px; border: 1px solid #bbb;
             border-radius: 3px; display: block; }
figcaption { font-size: 0.78em; color: #555; word-break: break-all;
             max-width: 360px; }
.empty { color: #777; font-style: italic; }
.dropped { color: #b26a00; font-size: 0.85em; }
@media (prefers-color-scheme: dark) {
  body { background: #1b1b1b; color: #e0e0e0; }
  th { background: #2a2a2a; }
  th, td { border-color: #444; }
  figcaption, .meta, .empty { color: #aaa; }
}
"""


def _badge(verdict: str) -> str:
    color = VERDICT_COLORS.get(verdict, DEFAULT_VERDICT_COLOR)
    return '<span class="badge" style="background:%s">%s</span>' % (
        color, html.escape(verdict or "?"))


def _status_cell(status: str) -> str:
    color = STATUS_COLORS.get(status, "#616161")
    return '<td style="color:%s;font-weight:bold">%s</td>' % (color, html.escape(status))


def _pre_block(lines: Sequence[str]) -> str:
    return "<pre>%s</pre>" % "\n".join(html.escape(l) for l in lines)


def render_run_html(run_id: str, result_obj, images: Sequence[str],
                    extraction: Dict, log_note: str = "") -> str:
    """The self-contained per-run contact sheet page. Pure: inputs in, HTML out."""
    entry = run_index_entry(result_obj, run_id)
    rows = verifier_rows(result_obj)
    parts: List[str] = []
    parts.append("<!doctype html>")
    parts.append('<html lang="en"><head><meta charset="utf-8">')
    parts.append('<meta name="viewport" content="width=device-width, initial-scale=1">')
    parts.append("<title>%s contact sheet</title>" % html.escape(run_id))
    parts.append("<style>%s</style></head><body>" % _PAGE_CSS)
    parts.append("<h1>%s %s</h1>" % (_badge(entry["verdict"]), html.escape(run_id)))
    meta = "scenario %s" % html.escape(entry["scenarioId"])
    if entry["subkind"]:
        meta += " &middot; subkind %s" % html.escape(entry["subkind"])
    if entry["utc"]:
        meta += " &middot; %s" % html.escape(entry["utc"])
    if entry["wallSeconds"] is not None:
        meta += " &middot; wall %ss" % int(entry["wallSeconds"])
    if entry["note"]:
        meta += " &middot; note: %s" % html.escape(entry["note"])
    parts.append('<p class="meta">%s &middot; <a href="%s">index</a></p>'
                 % (meta, INDEX_BASENAME))

    # Verifier verdict rows (the mechanical verdict, read in the same glance).
    parts.append("<h2>Verifier verdicts</h2>")
    if rows:
        parts.append("<table><tr><th>verifier</th><th>status</th><th>detail</th></tr>")
        for r in rows:
            parts.append("<tr><td>%s</td>%s<td>%s</td></tr>"
                         % (html.escape(r["name"]), _status_cell(r["status"]),
                            html.escape(r["note"])))
        parts.append("</table>")
    elif isinstance(result_obj, dict):
        # HONEST empty state (V3 live proof, 2026-07-31). An attempt that ends
        # BEFORE the verifier chain -- a refused run-lock, an invalid spec --
        # writes a perfectly readable result JSON whose `verifiers` is {}. The
        # first live non-PASS sheet rendered "result JSON missing or unreadable"
        # over exactly that file, which sends a reader hunting a file problem
        # that does not exist. Name the real reason instead.
        parts.append('<p class="empty">no verifier rows: this attempt ended before the '
                     'verifier chain ran (see the verdict badge and note above)</p>')
    else:
        parts.append('<p class="empty">no verifier rows (result JSON missing or unreadable)</p>')

    parts.append('<div class="columns">')

    # Screenshots grid (file references only; absent pre-V4 and that is fine).
    parts.append('<div class="shots"><h2>Screenshots (%d)</h2>' % len(images))
    if images:
        parts.append('<div class="grid">')
        shots_dir = run_id + SHOTS_SUFFIX
        for name in images:
            rel = "%s/%s" % (urllib.parse.quote(shots_dir), urllib.parse.quote(name))
            parts.append('<figure><a href="%s"><img src="%s" alt="%s" loading="lazy">'
                         "</a><figcaption>%s</figcaption></figure>"
                         % (rel, rel, html.escape(name), html.escape(name)))
        parts.append("</div>")
    else:
        parts.append('<p class="empty">no screenshots captured (the V4 capture verbs '
                     "feed this; until then the key log lines below are the sheet)</p>")
    parts.append("</div>")

    # Key log lines.
    parts.append('<div class="keylines"><h2>Key log lines</h2>')
    if log_note:
        parts.append('<p class="empty">%s</p>' % html.escape(log_note))
    dropped = extraction.get("dropped", {}) or {}

    parts.append("<h2>BATCH_COMPLETE (%d)</h2>" % len(extraction.get("batchComplete", [])))
    if extraction.get("batchComplete"):
        parts.append(_pre_block(extraction["batchComplete"]))
    else:
        parts.append('<p class="empty">none found</p>')
    if dropped.get("batchComplete"):
        parts.append('<p class="dropped">... %d more BATCH_COMPLETE line(s) not shown '
                     "(cap %d)</p>" % (dropped["batchComplete"], MAX_BATCH_LINES))

    parts.append("<h2>faithful-parity summaries (%d)</h2>" % len(extraction.get("parity", [])))
    if extraction.get("parity"):
        parts.append(_pre_block(extraction["parity"]))
    else:
        parts.append('<p class="empty">none found</p>')
    if dropped.get("parity"):
        parts.append('<p class="dropped">... %d more parity line(s) not shown (cap %d)</p>'
                     % (dropped["parity"], MAX_PARITY_LINES))

    anomalies = extraction.get("anomalies", []) or []
    parts.append("<h2>Anomaly raises (%d)</h2>" % len(anomalies))
    if anomalies:
        for a in anomalies:
            block: List[str] = []
            block.extend(html.escape(l) for l in a.get("before", []))
            block.append('<span class="anomaly">%s</span>' % html.escape(a.get("line", "")))
            block.extend(html.escape(l) for l in a.get("after", []))
            parts.append("<pre>%s</pre>" % "\n".join(block))
    else:
        parts.append('<p class="empty">none raised</p>')
    if dropped.get("anomalies"):
        parts.append('<p class="dropped">... %d more anomaly raise(s) not shown (cap %d)</p>'
                     % (dropped["anomalies"], MAX_ANOMALY_BLOCKS))

    parts.append("</div></div>")
    parts.append('<p class="meta">generated by harness/tools/contact_sheet.py '
                 "(V3, design-testing-unified &sect;6); log scanned: %d line(s)</p>"
                 % extraction.get("totalLines", 0))
    parts.append("</body></html>")
    return "\n".join(parts) + "\n"


def render_index_html(entries: Sequence[Dict]) -> str:
    """The results/index.html page: every run newest-first, verdict-colored."""
    ordered = sort_entries_newest_first(entries)
    parts: List[str] = []
    parts.append("<!doctype html>")
    parts.append('<html lang="en"><head><meta charset="utf-8">')
    parts.append('<meta name="viewport" content="width=device-width, initial-scale=1">')
    parts.append("<title>Parsek harness runs</title>")
    parts.append("<style>%s</style></head><body>" % _PAGE_CSS)
    parts.append("<h1>Parsek harness runs (%d)</h1>" % len(ordered))
    parts.append('<p class="meta">newest first; each run links to its contact sheet</p>')
    if not ordered:
        parts.append('<p class="empty">no run results found</p>')
    else:
        parts.append("<table><tr><th>run</th><th>verdict</th><th>scenario</th>"
                     "<th>ended (UTC)</th><th>wall</th><th>note</th></tr>")
        for e in ordered:
            run_id = str(e.get("runId") or "?")
            sheet = e.get("sheetHref")
            run_cell = ('<a href="%s">%s</a>' % (urllib.parse.quote(sheet), html.escape(run_id))
                        if sheet else html.escape(run_id))
            verdict = str(e.get("verdict") or "?")
            subkind = str(e.get("subkind") or "")
            verdict_cell = _badge(verdict)
            if subkind:
                verdict_cell += " <small>%s</small>" % html.escape(subkind)
            wall = e.get("wallSeconds")
            parts.append("<tr><td>%s</td><td>%s</td><td>%s</td><td>%s</td>"
                         "<td>%s</td><td>%s</td></tr>"
                         % (run_cell, verdict_cell,
                            html.escape(str(e.get("scenarioId") or "?")),
                            html.escape(str(e.get("utc") or "")),
                            ("%ds" % int(wall)) if wall is not None else "",
                            html.escape(str(e.get("note") or ""))))
        parts.append("</table>")
    parts.append('<p class="meta">generated by harness/tools/contact_sheet.py '
                 "(V3, design-testing-unified &sect;6)</p>")
    parts.append("</body></html>")
    return "\n".join(parts) + "\n"


# ---------------------------------------------------------------------------
# Thin I/O shell. Every reader is tolerant; every writer is tmp+rename so a
# crash mid-write never leaves a torn page.
# ---------------------------------------------------------------------------


def _read_result_json(path: str):
    """The parsed result JSON, or None when absent/unreadable/not a dict."""
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            obj = json.load(fh)
    except (OSError, ValueError):
        return None
    return obj if isinstance(obj, dict) else None


def _read_log_text(path: str) -> Optional[str]:
    """The collected KSP.log body, or None when absent/unreadable."""
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except OSError:
        return None


def list_run_images(shots_dir: str) -> List[str]:
    """Image filenames in a run's _shots dir, sorted by name. [] when absent."""
    if not os.path.isdir(shots_dir):
        return []
    out = []
    try:
        for name in sorted(os.listdir(shots_dir)):
            if name.lower().endswith(IMAGE_EXTENSIONS) and \
                    os.path.isfile(os.path.join(shots_dir, name)):
                out.append(name)
    except OSError:
        return out
    return out


def _write_text_atomic(path: str, text: str) -> None:
    # PID-suffixed tmp (adversarial review NIT 12): two run.py processes on
    # DIFFERENT instance profiles share results/ (the run lock is per-instance),
    # and both rewrite index.html -- a shared fixed tmp path could interleave
    # into a corrupt page; distinct tmps make the os.replace last-writer-wins.
    tmp = "%s.tmp.%d" % (path, os.getpid())
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def _is_run_result_name(name: str) -> bool:
    """True for a per-run result record filename. Excludes the OTHER JSON files
    the results dir holds: ``<runId>_mission.json`` (mission-result artifact)
    and ``<runId>.manifest.json`` (accumulated ledger manifest)."""
    return (name.endswith(".json")
            and not name.endswith("_mission.json")
            and not name.endswith(".manifest.json"))


def discover_run_ids(results_dir: str) -> List[str]:
    """Run ids that have a result JSON in ``results_dir`` (unsorted)."""
    if not os.path.isdir(results_dir):
        return []
    out = []
    for name in os.listdir(results_dir):
        if _is_run_result_name(name) and os.path.isfile(os.path.join(results_dir, name)):
            out.append(name[:-len(".json")])
    return out


def generate_run_sheet(results_dir: str, run_id: str) -> str:
    """Emit ``<results_dir>/<runId>_contact.html``. Safe on every partial-input
    combination; returns the sheet path."""
    result_obj = _read_result_json(os.path.join(results_dir, "%s.json" % run_id))
    shots_dir = os.path.join(results_dir, run_id + SHOTS_SUFFIX)
    log_text = _read_log_text(os.path.join(shots_dir, "KSP.log"))
    images = list_run_images(shots_dir)
    extraction = extract_key_log_lines(log_text)
    log_note = "" if log_text is not None else \
        "no collected KSP.log for this run (results/%s%s/KSP.log absent)" % (run_id, SHOTS_SUFFIX)
    page = render_run_html(run_id, result_obj, images, extraction, log_note)
    sheet_path = os.path.join(results_dir, run_id + CONTACT_SUFFIX)
    os.makedirs(results_dir, exist_ok=True)
    _write_text_atomic(sheet_path, page)
    return sheet_path


def generate_index(results_dir: str) -> str:
    """Emit ``<results_dir>/index.html`` over every run result JSON present.
    A malformed result JSON becomes a gray UNREADABLE row, never a crash."""
    entries: List[Dict] = []
    for run_id in discover_run_ids(results_dir):
        # Per-row isolation (review round-2 hardening note): the index walks
        # EVERY result JSON, so one poison row must degrade to UNREADABLE, not
        # block index.html for all subsequent runs -- regardless of which
        # exotic value class the row build trips on.
        try:
            obj = _read_result_json(os.path.join(results_dir, "%s.json" % run_id))
            entry = run_index_entry(obj, run_id)
            sheet_name = run_id + CONTACT_SUFFIX
            if os.path.isfile(os.path.join(results_dir, sheet_name)):
                entry["sheetHref"] = sheet_name
        except Exception:  # noqa: BLE001 - one bad row must not sink the index
            entry = {"runId": run_id, "scenarioId": "?", "verdict": "UNREADABLE",
                     "subkind": "", "utc": "", "wallSeconds": None,
                     "note": "index row build failed"}
        entries.append(entry)
    index_path = os.path.join(results_dir, INDEX_BASENAME)
    os.makedirs(results_dir, exist_ok=True)
    _write_text_atomic(index_path, render_index_html(entries))
    return index_path


def main(argv: Optional[Sequence[str]] = None) -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    default_results = os.path.join(os.path.dirname(here), "results")
    parser = argparse.ArgumentParser(
        description="V3 contact sheets: per-run HTML + newest-first index over harness/results/")
    parser.add_argument("--results-dir", default=default_results,
                        help="results directory (default: harness/results)")
    parser.add_argument("--run-id", help="generate one run's sheet (plus the index)")
    parser.add_argument("--index-only", action="store_true",
                        help="regenerate only index.html")
    args = parser.parse_args(argv)
    results_dir = os.path.abspath(args.results_dir)
    try:
        if not args.index_only:
            run_ids = [args.run_id] if args.run_id else sorted(discover_run_ids(results_dir))
            for run_id in run_ids:
                print(generate_run_sheet(results_dir, run_id))
        print(generate_index(results_dir))
    except Exception as exc:  # noqa: BLE001 - CLI wrapper; the run flow calls the functions directly
        print("contact_sheet: FAILED (%s: %s)" % (type(exc).__name__, exc), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
