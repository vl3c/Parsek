"""A7 + A8: the run index (runindex.py + tools/run_index.py) and lane health.

Pure cells over rows, the index text, the queries and the streaming KSP.log
scan; source-sync cells that read OUTSIDE harness/ (the C# literal of every
performance fact, ParsekLog's line format and rate-limit suffix, and the
committed tag map ``scripts/arch/log_tags.json``); shell cells over a synthetic
archive in a temp dir; git cells over a temp bare repo (never a real remote);
and fake-KSP smoke runs through run.py's retry loop that append to the index
of a TEMP umbrella and keep their verdict when the index fails.

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib
"""

import calendar
import contextlib
import gzip
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(HARNESS_ROOT)
TOOLS_DIR = os.path.join(HARNESS_ROOT, "tools")
for _p in (HARNESS_ROOT, HERE, TOOLS_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import runindex  # noqa: E402
import run  # noqa: E402
import run_index  # noqa: E402
from test_run_smoke import FakeRuntime, _make_spec, _write_manifest  # noqa: E402

LANE = "B11-mun-orbit"


def _pline(level, tag, msg, prefix="[LOG 12:00:00.000] "):
    return ("%s[Parsek][%s][%s] %s\n" % (prefix, level, tag, msg)).encode("utf-8")


def _result(run_id, verdict="PASS", attempt=1, started="2026-10-11T09:00:00Z", wall=100,
            lane=LANE, tier="daily", subkind="", note="", frames=None):
    out = {"schema": 1, "runId": run_id, "scenarioId": lane, "tier": tier,
           "instanceProfile": "stock-minimal", "startedUtc": started, "endedUtc": started,
           "wallSeconds": wall, "missionWallSeconds": None, "attempt": attempt,
           "verdict": verdict, "subkind": subkind, "note": note}
    if frames is not None:
        out["verifiers"] = {"unityExceptions": {"total": 0, "parsekFrames": frames}}
    return out


def _manifest(worktree=r"C:\Users\someone\Dev\Parsek-item9"):
    return {"schema": 1, "origin": "run", "worktree": worktree, "gitHead": "a" * 40,
            "parsekDllSha256": "b" * 64, "harnessLog": "slice"}


def _row(run_id, verdict="PASS", attempt=1, started="2026-10-11T09:00:00Z", wall=100, lane=LANE,
         health=None, **kw):
    row, reason = runindex.build_row(run_id, _result(run_id, verdict, attempt, started, wall, lane, **kw),
                                     _manifest(), health)
    assert reason is None, reason
    return row


def _rid(stamp, lane, attempt=1, claimed=(), min_ordinal=1):
    """A run id built the way run.py builds it: every attempt from its OWN
    start minute (run.py ``_run_id_stamp`` is "now" per attempt)."""
    return hlib.resolve_run_id(stamp, lane, set(claimed), attempt=attempt, min_ordinal=min_ordinal).run_id


def _iso(stamp):
    """``2026-10-11_0914`` -> ``2026-10-11T09:14:00Z``."""
    return "%sT%s:%s:00Z" % (stamp[:10], stamp[11:13], stamp[13:15])


def _attempt(stamp, lane, verdict, attempt=1, wall=100, **kw):
    """One archived attempt's row, its run id and start from ``stamp``."""
    return _row(_rid(stamp, lane, attempt), verdict, attempt, _iso(stamp), wall, lane, **kw)


def _health(warn=0, error=0, parsek=100, nbytes=10000):
    return {"bytes": nbytes, "lines": parsek * 2, "parsek": parsek, "unity": 0, "top": [],
            "verbose": parsek - warn - error, "info": 0, "warn": warn, "error": error}


# ---------------------------------------------------------------------------
# the KSP.log scan
# ---------------------------------------------------------------------------

class LogScanTests(unittest.TestCase):
    def test_levels_subsystems_bytes_and_top(self):
        lines = [
            b"[LOG 12:00:00.000] Loading assembly\n",
            _pline("INFO", "Flight", "start"),
            _pline("VERBOSE", "Flight", "frame"),
            _pline("WARN", "Ghost", "late", prefix="[WRN 12:00:01.000] "),
            _pline("ERROR", "Ghost", "boom", prefix="[ERR 12:00:01.000] "),
            _pline("VERBOSE", "Recorder", "x"),
            _pline("VERBOSE", "Flight", "y"),
        ]
        scan = runindex.scan_lines(lines)
        h = scan.compact()
        self.assertEqual(sum(len(l) for l in lines), h["bytes"])
        self.assertEqual(7, h["lines"])
        self.assertEqual(6, h["parsek"])
        self.assertEqual((3, 1, 1, 1), (h["verbose"], h["info"], h["warn"], h["error"]))
        self.assertEqual([["Flight", 3], ["Ghost", 2], ["Recorder", 1]], h["top"])
        self.assertEqual({"Flight": [3, 0, 0], "Ghost": [2, 1, 1], "Recorder": [1, 0, 0]},
                         scan.breakdown())
        self.assertEqual(len(lines[1]) + len(lines[2]) + len(lines[6]), scan.subsystems["Flight"][3])
        # No performance fact was logged, so none is present.
        for fact in runindex.HEALTH_FACTS:
            self.assertNotIn(fact.key, h)

    def test_a_line_with_no_newline_and_a_crlf_line_read_the_same(self):
        a = runindex.scan_lines([b"[Parsek][INFO][Tag] msg"]).compact()
        b = runindex.scan_lines([b"[Parsek][INFO][Tag] msg\r\n"]).compact()
        self.assertEqual((1, 1), (a["info"], b["info"]))
        self.assertEqual([["Tag", 1]], a["top"])

    def test_a_malformed_parsek_line_is_neither_parsek_nor_unity(self):
        scan = runindex.scan_lines([b"[Parsek][NOTICE][Tag] NullReferenceException\n",
                                    b"[Parsek] NullReferenceException in a Parsek line\n"])
        h = scan.compact()
        self.assertEqual((0, 0), (h["parsek"], h["unity"]))

    def test_unity_count_matches_hlibs_stack_scan(self):
        text = "\n".join([
            "[EXC 12:00:00.100] NullReferenceException: Object reference not set",
            "  at Parsek.Foo.Bar () [0x00000] in <x>:0",
            "[ERR 12:00:00.200] MissingReferenceException: gone",
            "IndexOutOfRangeException and NullReferenceException on one line",
            "ArgumentException: GUILayout: Mismatched LayoutGroup.Repaint",
            "[EXC 12:00:01.000] InvalidOperationException: frames-only opener",
            "Exception handling event onVesselChange in class X:System.KeyNotFoundException",
            "[LOG 12:00:02.000] [Parsek][WARN][Ghost] NullReferenceException mentioned by Parsek",
            "an ordinary line about Exceptions in general",
            "ArgumentException: something else",
        ]) + "\n"
        mine = runindex.scan_lines(io.BytesIO(text.encode("utf-8"))).compact()["unity"]
        theirs = hlib.scan_unity_exception_stacks(text)
        self.assertEqual(sum(theirs.counts.values()) + theirs.uncounted, mine)
        self.assertEqual(7, mine)

    def test_the_prefilter_can_never_hide_a_match(self):
        # unity_exception_hits skips a line holding neither "Exception" nor a
        # leading "[EXC": exact only while every pattern needs one of them.
        for name, pat in hlib.UNITY_EXCEPTION_PATTERNS:
            self.assertIn("Exception", pat.pattern, name)
        self.assertTrue(hlib.UNITY_EXC_RECORD.pattern.startswith(r"^\[EXC"))
        self.assertIn("Exception", hlib.UNITY_EVENT_HANDLER_EXCEPTION.pattern)

    def test_suppressed_suffix_counts_frames_and_keeps_the_regex_anchored(self):
        msg = "Playback frame budget exceeded: 21.4ms (12 ghosts, warp: 1x)"
        scan = runindex.scan_lines([
            _pline("WARN", "Diagnostics", msg),
            _pline("WARN", "Diagnostics", "Playback frame budget exceeded: 40.0ms (30 ghosts, warp: 4x)"
                   " | suppressed=57"),
            _pline("WARN", "Diagnostics", "Recording frame exceeded budget: 9.75ms for vessel \"A\" | suppressed=3"),
        ])
        h = scan.compact()
        self.assertEqual([2, 59, 40.0, 30], h["playback"])
        self.assertEqual([1, 4, 9.75], h["recording"])
        # An end-anchored fact still reads with a rate-limit suffix on it.
        h = runindex.scan_lines([_pline("VERBOSE", "Diagnostics", "Scene load complete (FLIGHT): 4 recordings, "
                                        "~1.2 MB memory est (9 pts, 4 evts, 3 segs), 2 ghosts | suppressed=5")]).compact()
        self.assertEqual([1, 4, 2], h["scene"])

    def test_a_fact_on_another_tag_is_not_read(self):
        h = runindex.scan_lines([_pline("VERBOSE", "Scenario", "OnLoad: 12ms (3 recordings) phase=x status=y")]).compact()
        self.assertNotIn("load", h)

    def test_every_fact_accumulates(self):
        h = runindex.scan_lines([
            _pline("VERBOSE", "Diagnostics", "OnLoad: 100ms (3 recordings) phase=cold status=ok"),
            _pline("VERBOSE", "Diagnostics", "OnLoad: 250ms (4 recordings) phase=warm status=ok"),
            _pline("VERBOSE", "Diagnostics", "OnSave: 7ms (1 dirty of 4)"),
            _pline("VERBOSE", "Diagnostics", "Scene load complete (FLIGHT): 4 recordings, ~1.2 MB memory est "
                                             "(900 pts, 40 evts, 3 segs), 2 ghosts"),
            _pline("VERBOSE", "Diagnostics", "Scene load complete (SPACECENTER): 9 recordings, ~2 MB memory est "
                                             "(9 pts, 4 evts, 3 segs), 0 ghosts"),
            _pline("VERBOSE", "Diagnostics", "Recording growth rate at stop: 512 points, 33 events, 61.0s "
                                             "elapsed, 8.39 pts/s, 0.54 evts/s, est 43520 bytes"),
            _pline("VERBOSE", "Diagnostics", "Recording growth rate at stop: 10 points, 1 events, 1.0s "
                                             "elapsed, 10.00 pts/s, 1.00 evts/s, est 1000 bytes"),
        ]).compact()
        self.assertEqual([2, 250, 350], h["load"])
        self.assertEqual([1, 7, 7], h["save"])
        self.assertEqual([2, 9, 2], h["scene"])
        self.assertEqual([2, 512, 44520], h["growth"])


class HealthFactSourceSyncTests(unittest.TestCase):
    """Each fact parser is pinned to the C# literal it reads: the fragments are
    found verbatim in the cited file, on the ``Diagnostics`` tag, and the line
    they render (format holes filled with the sample values) parses back."""

    HOLE = re.compile(r"\{(\d+)(?::[^}]*)?\}")

    def _source(self, rel):
        with open(os.path.join(REPO_ROOT, rel), "r", encoding="utf-8") as fh:
            return fh.read()

    def test_every_fragment_is_a_literal_in_the_cited_file_on_the_diagnostics_tag(self):
        for fact in runindex.HEALTH_FACTS:
            text = self._source(fact.source)
            for frag in fact.fragments:
                literal = '"' + frag + '"'
                self.assertIn(literal, text, "%s: %s no longer in %s" % (fact.key, frag, fact.source))
            wfile, wre = fact.writer
            self.assertIsNotNone(wre.search(self._source(wfile)),
                                 "%s: no ParsekLog call on %r in %s" % (fact.key, runindex.FACT_TAG, wfile))
            self.assertIn('"%s"' % runindex.FACT_TAG, wre.pattern, fact.key)

    def test_the_rendered_line_parses_back(self):
        for fact in runindex.HEALTH_FACTS:
            fmt = "".join(fact.fragments).replace('\\"', '"')
            message = self.HOLE.sub(lambda m: fact.sample_args[m.group(1)], fmt)
            m = fact.regex.match(message)
            self.assertIsNotNone(m, "%s: %r" % (fact.key, message))
            self.assertEqual(fact.expected, m.groups(), fact.key)
            h = runindex.scan_lines([_pline("VERBOSE", runindex.FACT_TAG, message)]).compact()
            self.assertIn(fact.key, h, fact.key)

    def test_parseklog_line_format_levels_and_suppressed_suffix(self):
        text = self._source("Source/Parsek/ParsekLog.cs")
        self.assertIn('$"[Parsek][{level}][{safeSubsystem}] {safeMessage}"', text)
        for level in runindex.LEVELS:
            self.assertIn('Write("%s", subsystem, message);' % level, text)
        levels = set(re.findall(r'Write\("([A-Z]+)"', text))
        self.assertEqual(set(runindex.LEVELS), levels)
        self.assertGreaterEqual(text.count('$" | suppressed={state.suppressedCount}"'), 4)
        self.assertIsNotNone(runindex.SUPPRESSED_RE.search("x | suppressed=12"))

    def test_not_logged_names_three_measurements_each_with_one_line(self):
        self.assertEqual(3, len(runindex.NOT_LOGGED))
        for what, line in runindex.NOT_LOGGED:
            self.assertTrue(line.startswith("[Parsek][INFO][Diagnostics] "), what)
            self.assertNotIn("\n", line)


class CommittedTagMapTests(unittest.TestCase):
    """The harness reads ``scripts/arch/log_tags.json`` as data (it never
    imports scripts/arch); its IL-side test is scripts/arch/test_iltags.py."""

    def test_the_committed_map_parses_and_names_modules(self):
        path = os.path.join(REPO_ROOT, "scripts", "arch", "log_tags.json")
        with open(path, "r", encoding="utf-8") as fh:
            doc = json.load(fh)
        self.assertEqual(runindex.TAG_MAP_SCHEMA, doc["schema"])
        mods = runindex.tag_modules(doc)
        self.assertGreater(len(mods), 100)
        self.assertEqual(len(doc["tags"]), doc["totals"]["tags"])
        self.assertIn("Diagnostics", mods)
        for tag, (primary, every) in mods.items():
            self.assertIn(primary, every, tag)


# ---------------------------------------------------------------------------
# rows and the index text
# ---------------------------------------------------------------------------

class RowTests(unittest.TestCase):
    RID = "2026-10-11_0900_" + LANE

    def test_full_row_keys_and_sources(self):
        row = _row(self.RID, health=_health(), frames=2)
        self.assertEqual({"schema", "runId", "lane", "tier", "profile", "started", "wall",
                          "attempt", "verdict", "commit", "dll", "worktree", "origin",
                          "harnessLog", "health", "final", "parsekFrames"}, set(row))
        self.assertEqual(LANE, row["lane"])
        self.assertEqual("a" * runindex.COMMIT_HEX, row["commit"])
        self.assertEqual("b" * runindex.DLL_HEX, row["dll"])
        full, _ = runindex.build_row(self.RID, _result(self.RID, "INVALID", subkind="boot", note="post-quit work raised OSError: x", frames=0),
                                     dict(_manifest(), harnessLog=None), None)
        full_result = _result(self.RID)
        full_result["missionWallSeconds"] = 42
        self.assertEqual(("boot", "post-quit work raised OSError"), (full["subkind"], full["note"]))
        self.assertNotIn("parsekFrames", full)
        self.assertNotIn("harnessLog", full)
        self.assertNotIn("missionWall", full)
        self.assertEqual(42, runindex.build_row(self.RID, full_result, _manifest(), None)[0]["missionWall"])
        self.assertEqual("Parsek-item9", row["worktree"])
        self.assertEqual(2, row["parsekFrames"])
        self.assertNotIn("gaps", row)
        line = runindex.row_line(row)
        self.assertNotIn(" ", line.replace("stock-minimal", ""))
        self.assertNotIn("Users", line)

    def test_missing_pieces_become_gaps_not_failures(self):
        result = _result(self.RID + "_a2", attempt=None)
        del result["startedUtc"]
        del result["scenarioId"]
        row, reason = runindex.build_row(self.RID + "_a2", result, None, None, ["no KSP.log.gz"])
        self.assertIsNone(reason)
        self.assertEqual(LANE, row["lane"])
        self.assertEqual(2, row["attempt"])
        self.assertEqual("2026-10-11T09:00:00Z", row["started"])
        self.assertEqual(["no KSP.log.gz", "no manifest", "no startedUtc"], row["gaps"])
        self.assertIsNone(row["health"])
        self.assertIsNone(row["commit"])

    def test_unreadable_results_are_skipped_with_a_reason(self):
        self.assertEqual("no readable result json", runindex.build_row(self.RID, None, None, None)[1])
        self.assertEqual("result json has no verdict",
                         runindex.build_row(self.RID, {"scenarioId": LANE}, None, None)[1])

    def test_a_worktree_that_is_a_run_folder_or_snapshot_is_none(self):
        rid = "2026-10-11_0900_" + LANE + "_a2"
        for path, want in ((r"D:\\Dev\\Parsek-item9", "Parsek-item9"),
                           ("/a/automation/results-archive/" + rid, None),
                           ("/a/logs/" + rid[:-3], None),
                           (r"C:\\x\\logs\\2026-10-11_0912_collect", None),
                           (None, None)):
            m = dict(_manifest(), origin="backfill", worktree=path)
            row, _r = runindex.build_row(rid, _result(rid, attempt=2), m, None)
            self.assertEqual(want, row["worktree"], path)
            self.assertNotIn("worktree withheld", row.get("gaps", []))

    def test_run_id_helpers(self):
        rid = "2026-10-11_0900_" + LANE + "_run2_a3"
        self.assertEqual(3, runindex.attempt_of(rid))
        self.assertEqual("2026-10-11_0900_" + LANE + "_run2", runindex.run_stem(rid))
        self.assertEqual(LANE, runindex.lane_from_run_id(rid))
        self.assertEqual("2026-10-11T09:00:00Z", runindex.started_from_run_id(rid))
        self.assertEqual("wt", runindex.folder_name("/home/u/wt/"))
        self.assertEqual("wt", runindex.folder_name(r"D:\a\wt"))
        self.assertIsNone(runindex.folder_name(None))


class IndexTextTests(unittest.TestCase):
    def setUp(self):
        self.a1 = _attempt("2026-10-11_0900", "X", "INVALID", subkind="boot-crash")
        self.a2 = _attempt("2026-10-11_0914", "X", "PASS", 2)
        self.b = _attempt("2026-10-10_0800", "Y", "PASS")

    def test_normalize_orders_and_derives_final_and_by_retry(self):
        rows = runindex.normalize([self.a2, self.a1, self.b])
        self.assertEqual(["2026-10-10_0800_Y", "2026-10-11_0900_X", "2026-10-11_0914_X_a2"],
                         [r["runId"] for r in rows])
        self.assertEqual([True, False, True], [r["final"] for r in rows])
        self.assertEqual([False, False, True], [r.get("byRetry", False) for r in rows])
        self.assertEqual([None, None, "2026-10-11_0900_X"], [r.get("retryOf") for r in rows])
        # The first attempt alone is final until its retry arrives.
        alone = runindex.normalize([self.a1])
        self.assertTrue(alone[0]["final"])

    def test_upsert_is_idempotent_byte_for_byte(self):
        rows, c1 = runindex.upsert([], [self.a1])
        self.assertEqual({"added": 1, "replaced": 0, "unchanged": 0}, c1)
        text = runindex.serialize_index(rows)
        rows2, c2 = runindex.upsert(runindex.parse_index(text).rows, [self.a1])
        self.assertEqual({"added": 0, "replaced": 0, "unchanged": 1}, c2)
        self.assertEqual(text, runindex.serialize_index(rows2))
        # The retry arriving later flips the first attempt's final, yet re-adding
        # the first attempt still reads unchanged (final is derived, not content).
        rows3, _ = runindex.upsert(rows2, [self.a2])
        rows4, c4 = runindex.upsert(rows3, [dict(self.a1)])
        self.assertEqual(1, c4["unchanged"])
        self.assertEqual(runindex.serialize_index(rows3), runindex.serialize_index(rows4))

    def test_upsert_replaces_a_changed_row(self):
        rows, _ = runindex.upsert([], [self.b])
        changed = dict(self.b, wall=999)
        rows, c = runindex.upsert(rows, [changed])
        self.assertEqual(1, c["replaced"])
        self.assertEqual(999, rows[0]["wall"])

    def test_merge_unions_by_run_id_and_local_wins(self):
        remote_b = dict(self.b, verdict="PARSEK-FAIL")
        merged = runindex.merge_rows([self.b, self.a1], [remote_b, self.a2])
        self.assertEqual(3, len(merged))
        self.assertEqual("PASS", [r for r in merged if r["runId"] == self.b["runId"]][0]["verdict"])
        self.assertFalse([r for r in merged if r["runId"] == self.a1["runId"]][0]["final"])

    def test_parse_tolerates_junk_lines_and_names_them(self):
        text = runindex.serialize_index([self.b]) + "not json\n\n" + json.dumps({"schema": "other"}) + "\n"
        parsed = runindex.parse_index(text)
        self.assertEqual(1, len(parsed.rows))
        self.assertEqual(2, len(parsed.dropped))
        self.assertEqual([], parsed.newer)
        self.assertIn("line 2 is not JSON", parsed.dropped[0])

    NEWER = '{"runId":"2026-10-10_0830_Z","schema":"parsek-run-index/2","shape":"new"}'
    NEWER_HEAD = '{"schema":"parsek-run-index/7"}'

    def test_a_newer_schema_line_is_kept_in_place_byte_for_byte(self):
        rows = runindex.normalize([self.b, self.a1])
        text = (self.NEWER_HEAD + "\n" + runindex.row_line(rows[0]) + "\n" + self.NEWER + "\n"
                + runindex.row_line(rows[1]) + "\n")
        parsed = runindex.parse_index(text)
        self.assertEqual(([self.b["runId"], self.a1["runId"]], 2, []),
                         ([r["runId"] for r in parsed.rows], len(parsed.newer), parsed.dropped))
        self.assertEqual(text, runindex.serialize_index(parsed.rows, parsed.newer))
        # An upsert that adds a row keeps both lines where they stood.
        merged, counts = runindex.upsert(parsed.rows, [self.a2])
        self.assertEqual(1, counts["added"])
        out = runindex.serialize_index(merged, parsed.newer).splitlines()
        self.assertEqual(self.NEWER_HEAD, out[0])
        self.assertEqual(self.NEWER, out[2])
        self.assertEqual([self.b["runId"], self.a1["runId"], self.a2["runId"]],
                         [json.loads(l)["runId"] for l in out if l not in (self.NEWER, self.NEWER_HEAD)])
        # Not read by a query.
        self.assertEqual(["X", "Y"], runindex.lanes_of(parsed.rows))
        self.assertEqual(2, runindex.summary(parsed.rows)["attempts"])

    def test_truncated_older_and_foreign_lines_are_dropped_and_counted(self):
        line = runindex.row_line(runindex.normalize([self.b])[0])
        text = "\n".join([line, line[:40], '{"schema":"parsek-run-index/0","runId":"x"}',
                          '{"schema":"parsek-run-index/2x","runId":"y"}',
                          '{"schema":"parsek-run-index/1"}']) + "\n"
        parsed = runindex.parse_index(text)
        self.assertEqual((1, 0, 4), (len(parsed.rows), len(parsed.newer), len(parsed.dropped)))
        self.assertEqual(line + "\n", runindex.serialize_index(parsed.rows, parsed.newer))

    def test_merge_newer_keeps_each_line_once_local_first(self):
        a = runindex.NewerLine(("2026-10-10T08:00:00Z", "r"), self.NEWER)
        b = runindex.NewerLine(None, self.NEWER)
        c = runindex.NewerLine(None, self.NEWER_HEAD)
        self.assertEqual([a, c], runindex.merge_newer([a], [b, c]))

    def test_serialization_is_deterministic_and_sorted(self):
        one = runindex.serialize_index([self.a2, self.b, self.a1])
        two = runindex.serialize_index([self.b, self.a1, self.a2])
        self.assertEqual(one, two)
        first = json.loads(one.splitlines()[0])
        self.assertEqual(sorted(first), list(first))


# ---------------------------------------------------------------------------
# queries
# ---------------------------------------------------------------------------

class QueryTests(unittest.TestCase):
    def setUp(self):
        rows = [
            _attempt("2026-08-01_0900", "L", "PASS", wall=100),
            _attempt("2026-10-01_0900", "L", "INVALID", wall=50, subkind="boot-crash"),
            _attempt("2026-10-01_0910", "L", "PASS", 2, wall=120),
            _attempt("2026-10-20_0900", "L", "INVALID", wall=300, subkind="tooling"),
            _attempt("2026-10-20_0912", "L", "PARSEK-FAIL", 2, wall=310, subkind="ledger"),
            _attempt("2026-10-21_0900", "M", "INVALID", wall=10, tier="nightly"),
        ]
        self.rows = runindex.normalize(rows)

    def test_epoch_day_and_percentiles(self):
        self.assertEqual(0, runindex._epoch_day("1970-01-01T00:00:00Z"))
        self.assertEqual(31 + 28, runindex._epoch_day("1970-03-01T00:00:00Z"))
        self.assertAlmostEqual(0.5, runindex._epoch_day("1970-01-01T12:00:00Z"))
        self.assertEqual(3, runindex.percentile([5, 1, 3, 2, 4], 50))
        self.assertEqual(5, runindex.percentile([5, 1, 3, 2, 4], 90))
        self.assertIsNone(runindex.percentile([], 50))
        self.assertEqual(2.5, runindex.median([1, 2, 3, 4]))

    def test_lane_report_windows_flake_and_last_pass(self):
        rep = runindex.lane_report(self.rows, "L")
        self.assertEqual("2026-10-21T09:00:00Z", rep["now"])
        self.assertEqual("2026-10-20_0912_L_a2", rep["lastRun"]["runId"])
        self.assertEqual("2026-10-01_0910_L_a2", rep["lastPass"]["runId"])
        all_ = rep["windows"]["all"]
        self.assertEqual((5, 2, 1, 2), (all_["attempts"], all_["passes"], all_["fails"], all_["invalids"]))
        self.assertEqual((3, 2, 1), (all_["scenarioRuns"], all_["finalPasses"], all_["flaked"]))
        self.assertEqual(0.333, all_["flakeRate"])
        self.assertEqual(4, rep["windows"]["30d"]["attempts"])
        self.assertEqual(5, rep["windows"]["90d"]["attempts"])
        self.assertEqual(120, rep["wallP50"])
        # An explicit now narrows the window.
        self.assertEqual(0, runindex.lane_report(self.rows, "L", now="2026-12-31T00:00:00Z")
                         ["windows"]["30d"]["attempts"])

    def test_summary_names_never_passed_and_last_not_pass(self):
        s = runindex.summary(self.rows, {"L": "daily", "M": "nightly", "N": "daily"})
        self.assertEqual(["M"], [x["lane"] for x in s["neverPassed"]])
        self.assertEqual({"L": "PARSEK-FAIL", "M": "INVALID"},
                         {x["lane"]: x["verdict"] for x in s["lastNotPass"]})
        self.assertEqual(["L"], [x["lane"] for x in s["flakiest"]])
        self.assertEqual("L", s["slowest"][0]["lane"])
        self.assertEqual(["N"], s["specsWithNoRun"])
        self.assertEqual({"daily": {"INVALID": 2, "PARSEK-FAIL": 1, "PASS": 2}, "nightly": {"INVALID": 1}},
                         s["byTier"])

    def test_lane_table(self):
        table = runindex.lane_table(self.rows, {"L": "daily", "N": "daily"}).splitlines()
        self.assertTrue(table[0].startswith("| Lane | Tier |"))
        self.assertEqual("| L | daily | PARSEK-FAIL(ledger) | 2026-10-01, aaaaaaaaa | 3 | 67% | 120 | 0 |", table[2])
        self.assertIn("| M | nightly | INVALID | never | 1 | 0% | 10 | 0 |", table)
        self.assertIn("| N | daily | (no run) | | 0 | | | 0 |", table)

    def test_trend_is_the_last_n_oldest_first(self):
        t = runindex.trend(self.rows, "L", 2)
        self.assertEqual(["2026-10-20_0900_L", "2026-10-20_0912_L_a2"], [x["runId"] for x in t])


class AttemptLinkTests(unittest.TestCase):
    """B1: a retry's run id carries its OWN start minute, so attempts link by
    ``link_attempts``' inference, never by run id stem. Every id here is built
    with ``hlib.resolve_run_id`` from a distinct per-attempt stamp."""

    def _by_id(self, rows):
        return {r["runId"]: r for r in runindex.normalize(rows)}

    def _pin(self, rows, retry_of, final):
        got = self._by_id(rows)
        self.assertEqual(retry_of, {k: v["retryOf"] for k, v in got.items() if "retryOf" in v})
        self.assertEqual(final, {k: v["final"] for k, v in got.items()})
        self.assertEqual({k for k, v in got.items() if v["final"] and v["attempt"] > 1},
                         {k for k, v in got.items() if v.get("byRetry")})
        return got

    def test_a_pipelined_retry_flies_after_another_lanes_rows(self):
        rows = [
            _attempt("2026-11-01_1000", "X-1", "INVALID", subkind="boot-crash"),
            _attempt("2026-11-01_1007", "Y-1", "PASS"),
            _attempt("2026-11-01_1020", "Z-1", "INVALID", subkind="tooling"),
            _attempt("2026-11-01_1031", "X-1", "PASS", 2),
            _attempt("2026-11-01_1045", "Z-1", "INVALID", 2, subkind="tooling"),
            _attempt("2026-11-01_1052", "Y-1", "PASS"),
        ]
        self.assertNotEqual(runindex.run_stem(rows[3]["runId"]), rows[0]["runId"])
        self._pin(rows,
                  {"2026-11-01_1031_X-1_a2": "2026-11-01_1000_X-1",
                   "2026-11-01_1045_Z-1_a2": "2026-11-01_1020_Z-1"},
                  {"2026-11-01_1000_X-1": False, "2026-11-01_1007_Y-1": True,
                   "2026-11-01_1020_Z-1": False, "2026-11-01_1031_X-1_a2": True,
                   "2026-11-01_1045_Z-1_a2": True, "2026-11-01_1052_Y-1": True})
        norm = runindex.normalize(rows)
        x = runindex.lane_report(norm, "X-1")["windows"]["all"]
        self.assertEqual((2, 1, 1, 1, 1.0), (x["attempts"], x["scenarioRuns"], x["finalPasses"],
                                             x["flaked"], x["flakeRate"]))
        z = runindex.lane_report(norm, "Z-1")["windows"]["all"]
        self.assertEqual((1, 0, 0, 0.0), (z["scenarioRuns"], z["finalPasses"], z["flaked"], z["flakeRate"]))
        table = runindex.lane_table(norm)
        self.assertIn("| X-1 | daily | PASS | 2026-11-01, aaaaaaaaa | 1 | 100% | 100 |", table)
        self.assertIn("| Z-1 | daily | INVALID(tooling) | never | 1 | 0% | 100 |", table)
        self.assertEqual(["X-1"], [f["lane"] for f in runindex.summary(norm)["flakiest"]])

    def test_two_back_to_back_runs_of_one_lane_each_retried(self):
        rows = [
            _attempt("2026-11-02_0900", "L-1", "INVALID", subkind="boot-crash"),
            _attempt("2026-11-02_0914", "L-1", "PASS", 2),
            _attempt("2026-11-02_1000", "L-1", "INVALID", subkind="boot-crash"),
            _attempt("2026-11-02_1013", "L-1", "INVALID", 2, subkind="boot-crash"),
        ]
        self._pin(rows,
                  {"2026-11-02_0914_L-1_a2": "2026-11-02_0900_L-1",
                   "2026-11-02_1013_L-1_a2": "2026-11-02_1000_L-1"},
                  {"2026-11-02_0900_L-1": False, "2026-11-02_0914_L-1_a2": True,
                   "2026-11-02_1000_L-1": False, "2026-11-02_1013_L-1_a2": True})
        norm = runindex.normalize(rows)
        c = runindex.lane_report(norm, "L-1")["windows"]["all"]
        self.assertEqual((4, 2, 1, 1, 0.5), (c["attempts"], c["scenarioRuns"], c["finalPasses"],
                                             c["flaked"], c["flakeRate"]))
        self.assertIn("| L-1 | daily | INVALID(boot-crash) | 2026-11-02, aaaaaaaaa | 2 | 50% | 100 |",
                      runindex.lane_table(norm))
        self.assertEqual([{"lane": "L-1", "runId": "2026-11-02_1013_L-1_a2", "verdict": "INVALID",
                           "subkind": "boot-crash"}], runindex.summary(norm)["lastNotPass"])

    def test_one_lane_twice_in_a_pipelined_selection_pairs_newest_first(self):
        first = _rid("2026-11-03_1000", "R-1")
        second = _rid("2026-11-03_1000", "R-1", claimed={first})
        self.assertEqual("2026-11-03_1000_R-1_run2", second)
        rows = [
            _row(first, "INVALID", 1, "2026-11-03T10:00:10Z", lane="R-1", subkind="boot-crash"),
            _row(second, "INVALID", 1, "2026-11-03T10:00:50Z", lane="R-1", subkind="boot-crash"),
            _row(_rid("2026-11-03_1020", "R-1", 2), "PASS", 2, "2026-11-03T10:20:00Z", lane="R-1"),
            _row(_rid("2026-11-03_1030", "R-1", 2, min_ordinal=2), "PASS", 2, "2026-11-03T10:30:00Z",
                 lane="R-1"),
        ]
        self._pin(rows,
                  {"2026-11-03_1020_R-1_a2": first, "2026-11-03_1030_R-1_run2_a2": second},
                  {first: False, second: False, "2026-11-03_1020_R-1_a2": True,
                   "2026-11-03_1030_R-1_run2_a2": True})

    def test_only_an_invalid_attempt_of_the_same_worktree_and_origin_is_retried(self):
        rows = [
            _attempt("2026-11-04_0900", "P-1", "INVALID", subkind="boot-crash"),
            _attempt("2026-11-04_0930", "P-1", "PASS"),
            _attempt("2026-11-04_0940", "P-1", "PASS", 2),
        ]
        other = _attempt("2026-11-04_0950", "P-1", "PASS", 2)
        other["worktree"] = "Parsek-other"
        backfill = _attempt("2026-11-04_0955", "P-1", "PASS", 2)
        backfill["origin"] = "backfill"
        got = self._pin(rows + [other, backfill],
                        {"2026-11-04_0940_P-1_a2": "2026-11-04_0900_P-1"},
                        {"2026-11-04_0900_P-1": False, "2026-11-04_0930_P-1": True,
                         "2026-11-04_0940_P-1_a2": True, "2026-11-04_0950_P-1_a2": True,
                         "2026-11-04_0955_P-1_a2": True})
        # An attempt 2 with no attempt 1 to join stands alone (a run of its own).
        self.assertTrue(got["2026-11-04_0950_P-1_a2"]["byRetry"])
        # A window that cuts the first attempt off counts the retry as its own run.
        cut = [r for r in runindex.normalize(rows) if r["started"] >= "2026-11-04T09:30"]
        self.assertEqual(2, runindex._counts(cut)["scenarioRuns"])

    def test_an_orphan_retry_never_joins_an_old_terminal_run(self):
        """The reviewer's reproduction: an attempt 2 whose attempt 1 is missing
        must not join a months-old INVALID attempt 1 of the lane."""
        old = _attempt("2026-09-01_1000", "X", "INVALID", subkind="admission")
        orphan = _attempt("2026-11-01_1014", "X", "PASS", 2)
        self.assertEqual("2026-11-01_1014_X_a2", orphan["runId"])
        for prior in (old, dict(old, subkind="boot-crash")):  # terminal; retryable but too old
            got = self._pin([prior, orphan], {},
                            {"2026-09-01_1000_X": True, "2026-11-01_1014_X_a2": True})
            c = runindex._counts(list(got.values()))
            self.assertEqual((2, 0, 0.0), (c["scenarioRuns"], c["flaked"], c["flakeRate"]))

    def test_the_time_bound_at_its_boundary(self):
        bound = runindex.RETRY_LINK_MAX_GAP_SECONDS
        a1 = _attempt("2026-11-06_0100", "T-1", "INVALID", wall=600, subkind="boot-crash")
        end = runindex._epoch_seconds(a1["started"]) + 600

        def retry_at(seconds_after_end, prior=a1):
            t = end + seconds_after_end
            day = "2026-11-06" if t < runindex._epoch_seconds("2026-11-07T00:00:00Z") else "2026-11-07"
            base = runindex._epoch_seconds(day + "T00:00:00Z")
            hh, rem = divmod(t - base, 3600)
            mm, ss = divmod(rem, 60)
            rid = _rid("%s_%02d%02d" % (day, hh, mm), "T-1", 2)
            a2 = _row(rid, "PASS", 2, "%sT%02d:%02d:%02dZ" % (day, hh, mm, ss), lane="T-1")
            return runindex.link_attempts([prior, a2])

        self.assertEqual(1, len(retry_at(bound)))
        self.assertEqual({}, retry_at(bound + 1))
        # No wall: the bound runs from the start.
        no_wall = dict(a1, wall=None)
        self.assertEqual(1, len(retry_at(bound - 600, no_wall)))
        self.assertEqual({}, retry_at(bound - 599, no_wall))

    def test_a_terminal_attempt_1_between_is_skipped(self):
        first = _rid("2026-11-07_1000", "S-1")
        second = _rid("2026-11-07_1000", "S-1", claimed={first})
        rows = [
            _row(first, "INVALID", 1, "2026-11-07T10:00:10Z", lane="S-1", subkind="boot-crash"),
            _row(second, "INVALID", 1, "2026-11-07T10:00:50Z", lane="S-1", subkind="fixture-stale"),
            _attempt("2026-11-07_1020", "S-1", "PASS", 2),
        ]
        self._pin(rows, {"2026-11-07_1020_S-1_a2": first},
                  {first: False, second: True, "2026-11-07_1020_S-1_a2": True})

    def test_append_in_any_order_equals_a_rebuild(self):
        rows = [
            _attempt("2026-11-08_0900", "A-1", "INVALID", subkind="boot-crash"),
            _attempt("2026-11-08_0905", "B-1", "INVALID", subkind="fixture-stale"),
            _attempt("2026-11-08_0912", "A-1", "PASS", 2),
            _attempt("2026-11-08_0930", "A-1", "INVALID", subkind="tooling"),
            _attempt("2026-11-08_0941", "A-1", "INVALID", 2, subkind="tooling"),
            _attempt("2026-12-30_0900", "A-1", "PASS", 2),
        ]
        rebuilt = runindex.serialize_index(rows)
        for order in (rows, list(reversed(rows)), rows[3:] + rows[:3]):
            index = []
            for row in order:
                index, _c = runindex.upsert(runindex.parse_index(runindex.serialize_index(index)).rows, [row])
            self.assertEqual(rebuilt, runindex.serialize_index(index))
        self.assertEqual(2, len(runindex.link_attempts(rows)))

    def test_backfilled_rows_with_no_worktree_link_only_to_each_other(self):
        def backfilled(stamp, verdict, attempt=1, worktree=None, **kw):
            row = _attempt(stamp, "W-1", verdict, attempt, **kw)
            row["origin"] = "backfill"
            row["worktree"] = worktree
            return row

        a1 = backfilled("2026-11-09_0900", "INVALID", subkind="boot-crash")
        a2 = backfilled("2026-11-09_0912", "PASS", 2)
        self._pin([a1, a2], {a2["runId"]: a1["runId"]}, {a1["runId"]: False, a2["runId"]: True})
        # A worktree-less retry never joins a row that has a worktree, and back.
        with_wt = backfilled("2026-11-09_1000", "INVALID", worktree="Parsek", subkind="boot-crash")
        orphan = backfilled("2026-11-09_1012", "PASS", 2)
        with_wt2 = backfilled("2026-11-09_1100", "INVALID", subkind="boot-crash")
        orphan2 = backfilled("2026-11-09_1112", "PASS", 2, worktree="Parsek")
        self.assertEqual({}, runindex.link_attempts([with_wt, orphan]))
        self.assertEqual({}, runindex.link_attempts([with_wt2, orphan2]))

    def test_derived_keys_are_not_content(self):
        a1 = _attempt("2026-11-05_0900", "Q-1", "INVALID", subkind="boot-crash")
        a2 = _attempt("2026-11-05_0911", "Q-1", "PASS", 2)
        rows, _ = runindex.upsert([], [a1, a2])
        again, counts = runindex.upsert(rows, [dict(a1), dict(a2)])
        self.assertEqual(2, counts["unchanged"])
        self.assertEqual(runindex.serialize_index(rows), runindex.serialize_index(again))


class RetryLinkBoundCoversWorstLaneTests(unittest.TestCase):
    """The link bound must cover the gap a pipelined retry really has: the
    whole lane in flight when attempt 1's verdict arrived (its budget plus
    staging, boot, verifiers) plus the retry's own staging and boot. A spec
    whose budget outgrows it would leave its retries unlinked, so this reds
    here, the way LeaseCoversWorstCaseScenarioTests pins the lock lease."""

    def test_the_bound_exceeds_the_worst_committed_spec(self):
        specs = run.load_all_specs()
        self.assertTrue(specs, "no committed specs loaded; the invariant is vacuous")
        worst_id, worst = max(((s.get("id"), float((s.get("runtime", {}) or {}).get("budgetSeconds", 600)))
                               for s in specs), key=lambda x: x[1])
        needed = worst + 2 * runindex.RETRY_LINK_LANE_OVERHEAD_SECONDS
        self.assertGreater(runindex.RETRY_LINK_MAX_GAP_SECONDS, needed,
                           "retry link bound %ds must exceed %s's %.0fs budget + 2 x %ds overhead"
                           % (runindex.RETRY_LINK_MAX_GAP_SECONDS, worst_id, worst,
                              runindex.RETRY_LINK_LANE_OVERHEAD_SECONDS))


class NoteTests(unittest.TestCase):
    """A row keeps a result note only when it is one run.py / hlib write, in
    its fixed shape; anything else is withheld."""

    def test_each_producer_shape(self):
        cases = {
            "flakedThenPassed": "flakedThenPassed",
            runindex.NOTE_TESTS_BUILD: runindex.NOTE_TESTS_BUILD,
            runindex.NOTE_LOCK_HELD: runindex.NOTE_LOCK_HELD,
            "machine lock lost mid-selection after 3 scenario(s)": "machine lock lost mid-selection after 3 scenario(s)",
            "post-quit work raised OSError: [Errno 13] Permission denied: 'C:\\Users\\x\\r.json'":
                "post-quit work raised OSError",
            "post-quit work raised RuntimeError: line1\n/home/x": "post-quit work raised RuntimeError",
            "post-quit work raised KeyError": "post-quit work raised KeyError",
            ("this instance's Parsek.dll is not the build stock-minimal carries; provision it: "
             + hlib.provision_command_hint("other")): "this instance's Parsek.dll is not the build stock-minimal carries",
        }
        for note, kept in cases.items():
            self.assertEqual((kept, False), runindex.safe_note(note), note)
        for note in ("free text", "flakedThenPassed /x", "post-quit work raised a/b: x",
                     "machine lock lost mid-selection after x scenario(s)"):
            self.assertEqual((None, True), runindex.safe_note(note), note)
        self.assertEqual((None, False), runindex.safe_note(""))


class NoteProducerSourceSyncTests(unittest.TestCase):
    """Every result note producer in run.py and hlib, walked from the AST, is
    one NOTE_RULES keeps: a new producer, or a reworded one, reds here instead
    of every such row reading ``note withheld``."""

    @staticmethod
    def _tree(path):
        import ast
        with open(path, encoding="utf-8") as fh:
            return ast.parse(fh.read())

    def test_run_py_note_producers(self):
        import ast
        tree = self._tree(os.path.join(HARNESS_ROOT, "run.py"))
        kw, keys, stores = [], [], []
        for node in ast.walk(tree):
            if isinstance(node, ast.Call):
                kw.extend(k.value for k in node.keywords if k.arg == "note")
            elif isinstance(node, ast.Dict):
                keys.extend(v for k, v in zip(node.keys, node.values)
                            if isinstance(k, ast.Constant) and k.value == "note")
            elif isinstance(node, ast.Assign):
                for t in node.targets:
                    if isinstance(t, ast.Subscript) and isinstance(t.slice, ast.Constant) \
                            and t.slice.value == "note":
                        stores.append(node.value)
        src = [ast.unparse(v) for v in kw + keys + stores]
        # _post_quit_failure / admission (keywords), the result dict's verdict.note,
        # the selection refusal's note and the invalid-spec "" (dict keys), and
        # finish's flakedThenPassed (the one store).
        self.assertEqual(["what", "note", "verdict.note", "note", "''", "'flakedThenPassed'"], src)
        with open(os.path.join(HARNESS_ROOT, "run.py"), encoding="utf-8") as fh:
            text = fh.read()
        for literal in ('"post-quit work raised %s: %s" % (type(exc).__name__, exc)',
                        '"this instance\'s Parsek.dll is not the build %s carries; "',
                        'note = "%s"' % runindex.NOTE_TESTS_BUILD,
                        'holder_note = "%s"' % runindex.NOTE_LOCK_HELD,
                        'note = "machine lock lost mid-selection after %d scenario(s)" % ran_count'):
            self.assertIn(literal, text)
        self.assertEqual(2, text.count('note = "machine lock lost mid-selection after %d scenario(s)" % ran_count'))

    def test_hlib_verdict_notes(self):
        import ast
        notes = []
        for node in ast.walk(self._tree(os.path.join(HERE, "hlib.py"))):
            if isinstance(node, ast.Call) and getattr(node.func, "id", None) == "Verdict":
                if len(node.args) >= 6:
                    notes.append(ast.unparse(node.args[5]))
                notes.extend(ast.unparse(k.value) for k in node.keywords if k.arg == "note")
        self.assertEqual(sorted(["base.note", "base.note", "'flakedThenPassed'"]), sorted(notes))


class HealthRegressionTests(unittest.TestCase):
    def _passes(self, values, lane="L"):
        rows = []
        for i, (warn, error, wall, parsek, nbytes) in enumerate(values):
            rows.append(_row("2026-10-%02d_0900_%s" % (i + 1, lane), "PASS", 1,
                             "2026-10-%02dT09:00:00Z" % (i + 1), wall, lane,
                             health=_health(warn, error, parsek, nbytes)))
        return rows

    def test_steady_lane_reads_clean(self):
        rows = self._passes([(2, 0, 100, 1000, 9000)] * 6)
        self.assertEqual([], runindex.health_regressions(rows))

    def test_each_rule_fires_alone(self):
        base = [(2, 0, 100, 1000, 9000)] * 5
        cases = {
            "warn 3 > median 2": (3, 0, 100, 1000, 9000),
            "error 1 > median 0": (2, 1, 100, 1000, 9000),
            "wall 130s >= 1.3x median 100s": (2, 0, 130, 1000, 9000),
            "bytes 11700 >= 1.3x median 9000": (2, 0, 100, 1000, 11700),
            "parsek 1300 >= 1.3x median 1000": (2, 0, 100, 1300, 9000),
        }
        for reason, latest in cases.items():
            out = runindex.health_regressions(self._passes(base + [latest]))
            self.assertEqual(1, len(out), reason)
            self.assertEqual([reason], out[0]["reasons"])
            self.assertEqual(5, len(out[0]["priorRuns"]))

    def test_just_under_the_growth_bound_is_clean(self):
        rows = self._passes([(2, 0, 100, 1000, 9000)] * 5 + [(2, 0, 129, 1299, 11699)])
        self.assertEqual([], runindex.health_regressions(rows))

    def test_only_the_previous_five_passes_count_and_failures_are_ignored(self):
        # Prior five warn [3,3,3,1,1] -> median 3, so a latest 3 is clean; a
        # sixth (warn 1) would drop the median to 2 and flag it.
        old = [(1, 0, 100, 1000, 9000)] * 3
        base = [(3, 0, 100, 1000, 9000)] * 3 + [(1, 0, 100, 1000, 9000)] * 2
        clean = self._passes(old + base + [(3, 0, 100, 1000, 9000)])
        self.assertEqual([], runindex.health_regressions(clean))
        base = [(2, 0, 100, 1000, 9000)] * 5
        rows = self._passes(old + base + [(3, 0, 100, 1000, 9000)])
        rows.append(_row("2026-10-30_0900_L", "PARSEK-FAIL", 1, "2026-10-30T09:00:00Z", 999, "L",
                         health=_health(99, 9)))
        out = runindex.health_regressions(rows)
        self.assertEqual(["warn 3 > median 2"], out[0]["reasons"])

    def test_too_few_prior_passes_reads_nothing(self):
        rows = self._passes([(2, 0, 100, 1000, 9000)] * 2 + [(9, 9, 900, 9000, 90000)])
        self.assertEqual([], runindex.health_regressions(rows))
        rows = self._passes([(2, 0, 100, 1000, 9000)] * 3 + [(9, 9, 900, 9000, 90000)])
        self.assertEqual(1, len(runindex.health_regressions(rows)))


# ---------------------------------------------------------------------------
# lane health
# ---------------------------------------------------------------------------

TAG_MAP = {"schema": runindex.TAG_MAP_SCHEMA, "totals": {"tags": 3},
           "tags": {"Flight": {"modules": {"Controllers": 5, "Recording": 2}},
                    "Ghost": {"modules": {"Playback": 3}},
                    "Shared": {"modules": {"A": 1, "B": 1}}}}


class LaneHealthTests(unittest.TestCase):
    def test_tag_modules_primary_is_most_sites_ties_by_name(self):
        mods = runindex.tag_modules(TAG_MAP)
        self.assertEqual(("Controllers", ("Controllers", "Recording")), mods["Flight"])
        self.assertEqual("A", mods["Shared"][0])
        self.assertEqual({}, runindex.tag_modules({"schema": "other"}))

    def test_entry_credits_primary_lists_shared_and_unmapped(self):
        row = _row("2026-10-11_0900_L", lane="L")
        entry = runindex.lane_health_entry(row, {"Flight": [10, 1, 0], "Ghost": [4, 0, 1], "Zed": [2, 0, 0]},
                                           runindex.tag_modules(TAG_MAP))
        self.assertEqual({"Controllers": [10, 1, 0], "Playback": [4, 0, 1]}, entry["modules"])
        self.assertEqual({"Flight": [10, 1, 0], "Ghost": [4, 0, 1], "Zed": 2}, entry["subsystems"])
        self.assertEqual([2, 0, 0], runindex.triple(entry["subsystems"]["Zed"]))
        self.assertEqual({"Recording": 10}, entry["sharedTagModules"])
        self.assertEqual({"Zed": 2}, entry["unmapped"])
        doc = runindex.build_lane_health({"L": entry}, TAG_MAP)
        self.assertEqual({"Zed": [1, 2]}, doc["unmappedTags"])
        self.assertEqual(3, doc["tagMap"]["tags"])
        found = runindex.lanes_for(doc, module="Recording")
        self.assertEqual([{"lane": "L", "lines": 10, "warn": None, "error": None,
                           "runId": "2026-10-11_0900_L", "sharedTagOnly": True}], found)
        self.assertEqual(4, runindex.lanes_for(doc, subsystem="Ghost")[0]["lines"])
        self.assertEqual([], runindex.lanes_for(doc, module="Nope"))
        self.assertEqual("Controllers", runindex.lane_top_modules(doc, "L")[0][0])

    def test_lanes_for_floor_keeps_warnings_and_counts_the_rest(self):
        mods = runindex.tag_modules(TAG_MAP)
        tag = sorted(mods)[0]
        module = mods[tag][0]
        entries = {}
        for lane, counts in (("boot-only", [2, 0, 0]), ("at-floor", [5, 0, 0]), ("busy", [40, 0, 0]),
                             ("warned", [1, 1, 0])):
            row = _row("2026-10-11_0900_" + lane, lane=lane)
            entries[lane] = runindex.lane_health_entry(row, {tag: counts}, mods)
        doc = runindex.build_lane_health(entries, TAG_MAP)
        found, below = runindex.lanes_for_below(doc, module=module, min_lines=runindex.LANES_FOR_MIN_LINES)
        self.assertEqual((["busy", "at-floor", "warned"], 1), ([f["lane"] for f in found], below))
        found, below = runindex.lanes_for_below(doc, subsystem=tag, min_lines=0)
        self.assertEqual((["busy", "at-floor", "boot-only", "warned"], 0), ([f["lane"] for f in found], below))
        self.assertEqual(5, runindex.LANES_FOR_MIN_LINES)

    def test_merge_keeps_the_newer_lane_entry_and_local_on_a_tie(self):
        mods = runindex.tag_modules(TAG_MAP)
        old = runindex.lane_health_entry(_row("2026-10-01_0900_L", started="2026-10-01T09:00:00Z", lane="L"),
                                         {"Flight": [1, 0, 0]}, mods)
        new = runindex.lane_health_entry(_row("2026-10-05_0900_L", started="2026-10-05T09:00:00Z", lane="L"),
                                         {"Flight": [9, 0, 0]}, mods)
        other_lane = runindex.lane_health_entry(_row("2026-10-02_0900_M", lane="M"), {"Ghost": [1, 0, 0]}, mods)
        local = runindex.build_lane_health({"L": old}, TAG_MAP)
        remote = runindex.build_lane_health({"L": new, "M": other_lane}, TAG_MAP)
        merged = runindex.merge_lane_health(local, remote)
        self.assertEqual("2026-10-05_0900_L", merged["lanes"]["L"]["runId"])
        self.assertIn("M", merged["lanes"])
        tie_local = dict(old, modules={"X": [7, 0, 0]})
        merged = runindex.merge_lane_health(runindex.build_lane_health({"L": tie_local}, TAG_MAP),
                                            runindex.build_lane_health({"L": old}, TAG_MAP))
        self.assertEqual({"X": [7, 0, 0]}, merged["lanes"]["L"]["modules"])
        self.assertEqual(9, runindex.merge_lane_health(remote, local)["lanes"]["L"]["subsystems"]["Flight"])
        self.assertIsNone(runindex.merge_lane_health(None, None))


# ---------------------------------------------------------------------------
# the tool over a synthetic archive
# ---------------------------------------------------------------------------

def _write_run(archive, run_id, result, manifest=True, log_lines=None, raw_gz=None):
    d = os.path.join(archive, run_id)
    os.makedirs(d, exist_ok=True)
    if result is not None:
        with open(os.path.join(d, run_id + ".json"), "w", encoding="utf-8") as fh:
            json.dump(result, fh)
    if manifest:
        with open(os.path.join(d, "manifest.json"), "w", encoding="utf-8") as fh:
            json.dump(_manifest(), fh)
    if log_lines is not None:
        with gzip.open(os.path.join(d, "KSP.log.gz"), "wb") as fh:
            fh.write(b"".join(log_lines))
    if raw_gz is not None:
        with open(os.path.join(d, "KSP.log.gz"), "wb") as fh:
            fh.write(raw_gz)


class ToolTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-runindex-")
        self.archive = os.path.join(self.tmp, "results-archive")
        self.index = os.path.join(self.archive, "index.jsonl")
        log = [_pline("INFO", "Flight", "hello"), _pline("WARN", "Ghost", "w"),
               _pline("VERBOSE", "Diagnostics", "OnSave: 7ms (1 dirty of 4)")]
        _write_run(self.archive, "2026-10-11_0900_L", _result("2026-10-11_0900_L", "INVALID", lane="L", subkind="tooling"),
                   log_lines=log)
        _write_run(self.archive, "2026-10-11_0914_L_a2",
                   _result("2026-10-11_0914_L_a2", "PASS", 2, "2026-10-11T09:14:00Z", lane="L"), log_lines=log)
        _write_run(self.archive, "2026-10-12_0900_M",
                   _result("2026-10-12_0900_M", started="2026-10-12T09:00:00Z", lane="M"), manifest=False)
        _write_run(self.archive, "2026-10-12_1000_N", None)
        _write_run(self.archive, "2026-10-12_1100_O",
                   _result("2026-10-12_1100_O", started="2026-10-12T11:00:00Z", lane="O"),
                   raw_gz=b"\x1f\x8b\x08\x00garbage")
        os.makedirs(os.path.join(self.archive, ".2026-10-12_1200_P.archive-tmp"))

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _main(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = run_index.main(list(argv))
        return rc, out.getvalue()

    def test_rebuild_reads_every_run_and_reports_skips_and_gaps(self):
        rc, out = self._main("rebuild", "--archive", self.archive, "--health-out",
                             os.path.join(self.tmp, "lh.json"))
        self.assertEqual(0, rc, out)
        parsed = run_index.load_index(self.index)
        rows = parsed.rows
        self.assertEqual(([], []), (parsed.dropped, parsed.newer))
        self.assertEqual(["2026-10-11_0900_L", "2026-10-11_0914_L_a2", "2026-10-12_0900_M",
                          "2026-10-12_1100_O"], [r["runId"] for r in rows])
        self.assertIn("2026-10-12_1000_N", out)
        self.assertIn("result json missing", out)
        self.assertIn(".2026-10-12_1200_P.archive-tmp", out)
        by_id = {r["runId"]: r for r in rows}
        self.assertEqual([1, 7, 7], by_id["2026-10-11_0914_L_a2"]["health"]["save"])
        self.assertEqual(["no KSP.log.gz", "no manifest"], by_id["2026-10-12_0900_M"]["gaps"])
        self.assertTrue(any(g.startswith("KSP.log.gz unreadable") for g in by_id["2026-10-12_1100_O"]["gaps"]))
        with open(os.path.join(self.tmp, "lh.json")) as fh:
            lh = json.load(fh)
        self.assertEqual(["L"], sorted(lh["lanes"]))
        self.assertEqual({"Diagnostics": 1, "Flight": 1, "Ghost": [1, 1, 0]},
                         lh["lanes"]["L"]["subsystems"])
        # No absolute path rides in the index.
        with open(self.index) as fh:
            text = fh.read()
        self.assertNotIn(self.tmp, text)
        self.assertNotIn("Users", text)

    def test_append_is_idempotent_on_disk(self):
        counts, skipped, path = run_index.append_runs(self.archive, ["2026-10-11_0900_L"])
        self.assertEqual((1, 0, 0), (counts["added"], counts["replaced"], counts["unchanged"]))
        with open(path, "rb") as fh:
            first = fh.read()
        mtime = os.stat(path).st_mtime_ns
        counts, skipped, _ = run_index.append_runs(self.archive, ["2026-10-11_0900_L"])
        self.assertEqual(1, counts["unchanged"])
        with open(path, "rb") as fh:
            self.assertEqual(first, fh.read())
        self.assertEqual(mtime, os.stat(path).st_mtime_ns)
        counts, skipped, _ = run_index.append_runs(self.archive, ["2026-10-11_0914_L_a2", "2026-10-12_1000_N"])
        self.assertEqual(1, counts["added"])
        self.assertEqual([("2026-10-12_1000_N", "result json missing")], skipped)
        rows = run_index.load_index(path).rows
        self.assertEqual([False, True], [r["final"] for r in rows])
        self.assertEqual("2026-10-11_0900_L", rows[1]["retryOf"])

    def test_append_keeps_newer_schema_lines_and_drops_unreadable_ones(self):
        newer = '{"runId":"2026-10-11_0800_Q","schema":"parsek-run-index/2"}'
        with open(self.index, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(newer + "\n" + '{"schema":"parsek-run-index/1","runId":"2026-10-11_08' + "\n")
        counts, _skipped, path = run_index.append_runs(self.archive, ["2026-10-11_0900_L"])
        self.assertEqual((1, 1, 1), (counts["added"], counts["keptNewer"], counts["dropped"]))
        line = run_index.format_append_counts(counts)
        self.assertIn("keptNewerSchema=1", line)
        self.assertIn("droppedUnreadable=1", line)
        with open(path, encoding="utf-8") as fh:
            lines = fh.read().splitlines()
        self.assertEqual(newer, lines[0])
        self.assertEqual(["2026-10-11_0900_L"], [json.loads(l)["runId"] for l in lines[1:]])
        # The truncated line is gone; a second append drops nothing and keeps the newer line.
        counts, _s, _p = run_index.append_runs(self.archive, ["2026-10-11_0900_L"])
        self.assertEqual((0, 1, 0, 1), (counts["added"], counts["unchanged"], counts["dropped"], counts["keptNewer"]))
        self.assertNotIn("dropped", run_index.format_append_counts(counts))
        # A truncated tail with nothing new to add is still dropped (the count is true).
        with open(path, "a", encoding="utf-8", newline="\n") as fh:
            fh.write('{"schema":"parsek-run-index/1","ru\n')
        counts, _s, _p = run_index.append_runs(self.archive, ["2026-10-11_0900_L"])
        self.assertEqual((0, 1), (counts["added"], counts["dropped"]))
        with open(path, encoding="utf-8") as fh:
            self.assertEqual(2, len(fh.read().splitlines()))
        # A rebuild keeps it too.
        self.assertEqual(0, self._main("rebuild", "--archive", self.archive)[0])
        with open(path, encoding="utf-8") as fh:
            self.assertEqual(newer, fh.read().splitlines()[0])

    def test_no_row_skip_reason_or_health_value_carries_a_path(self):
        """B3: an unreadable manifest / result / log costs a gap or a skip
        naming the exception TYPE only (an OSError's text holds the path)."""
        rid_m, rid_r = "2026-10-13_0900_U", "2026-10-13_0910_V"
        _write_run(self.archive, rid_m, _result(rid_m, started="2026-10-13T09:00:00Z", lane="U"),
                   manifest=False, log_lines=[_pline("INFO", "Flight", "x")])
        os.makedirs(os.path.join(self.archive, rid_m, "manifest.json"))
        _write_run(self.archive, rid_r, None)
        os.makedirs(os.path.join(self.archive, rid_r, rid_r + ".json"))
        rid_l = "2026-10-13_0920_W"
        _write_run(self.archive, rid_l, _result(rid_l, started="2026-10-13T09:20:00Z", lane="W"),
                   raw_gz=gzip.compress(b"[Parsek][INFO][Flight] a\n")[:-12])
        # run.py's real post-quit note (_post_quit_failure), over a Windows and a
        # POSIX path, and a result / manifest whose free text holds paths.
        post_quit = {}
        for rid, path in (("2026-10-13_0930_PQW", r"C:\Users\someone\Dev\Parsek\harness\results\x.json"),
                          ("2026-10-13_0940_PQP", "/home/someone/Parsek/harness/results/x.json")):
            exc = FileNotFoundError(2, "No such file or directory", path)
            note = "post-quit work raised %s: %s" % (type(exc).__name__, exc)
            self.assertIn("someone", note)
            post_quit[rid] = note
            res = _result(rid, "INVALID", started="2026-10-13T%s:%s:00Z" % (rid[11:13], rid[13:15]),
                          lane=rid[16:], subkind="post-quit-error", note=note)
            _write_run(self.archive, rid, res)
        rid_f = "2026-10-13_0950_F"
        res = _result(rid_f, "PASS", started="2026-10-13T09:50:00Z", lane="/home/someone/F",
                      subkind=r"C:\Users\someone", note="free text in /home/someone/x")
        res["tier"] = "daily /tmp"
        res["instanceProfile"] = "D:\\KSP"
        res["startedUtc"] = "2026-10-13 /x"
        _write_run(self.archive, rid_f, res,
                   log_lines=[_pline("INFO", "Flight", "x"), _pline("WARN", "/home/someone/tag", "y")])
        with open(os.path.join(self.archive, rid_f, "manifest.json"), "w", encoding="utf-8") as fh:
            json.dump({"schema": 1, "origin": "run /x", "worktree": "/home/someone/",
                       "gitHead": "C:\\Users\\x", "parsekDllSha256": "b" * 64, "harnessLog": "/x"}, fh)
        lh = os.path.join(self.tmp, "lh.json")
        rc, out = self._main("rebuild", "--archive", self.archive, "--health-out", lh)
        self.assertEqual(0, rc, out)
        rows = {r["runId"]: r for r in run_index.load_index(self.index).rows}
        for rid in post_quit:
            self.assertEqual("post-quit work raised FileNotFoundError", rows[rid]["note"])
            self.assertEqual("post-quit-error", rows[rid]["subkind"])
        f = rows[rid_f]
        self.assertEqual("F", f["lane"])
        self.assertEqual("someone", f["worktree"])
        for key in ("note", "subkind", "harnessLog"):
            self.assertNotIn(key, f)
        for key in ("tier", "profile", "origin", "commit"):
            self.assertIsNone(f[key], key)
        self.assertEqual("2026-10-13T09:50:00Z", f["started"])
        self.assertEqual({"commit withheld", "harnessLog withheld", "note withheld", "origin withheld",
                          "profile withheld", "scenarioId withheld", "startedUtc withheld",
                          "subkind withheld", "tier withheld"}, set(f["gaps"]))
        with open(lh, encoding="utf-8") as fh:
            self.assertEqual({"Flight": 1, runindex.OTHER_TAG: [1, 1, 0]}, json.load(fh)["lanes"]["F"]["subsystems"])
        # IsADirectoryError on POSIX, PermissionError on Windows.
        (gap,) = [g for g in rows[rid_m]["gaps"] if g.startswith("manifest")]
        self.assertRegex(gap, r"^manifest unreadable \((IsADirectoryError|PermissionError)\)$")
        self.assertIn("KSP.log.gz unreadable (EOFError)", rows[rid_l]["gaps"])
        _row_, reason, _b = run_index.read_run(self.archive, rid_r)
        self.assertRegex(reason, r"^result json unreadable \((IsADirectoryError|PermissionError)\)$")
        with open(lh, encoding="utf-8") as fh:
            health = json.load(fh)

        def strings(value, key=None):
            if isinstance(value, dict):
                for k, v in value.items():
                    yield k
                    yield from strings(v, k)
            elif isinstance(value, list):
                for v in value:
                    yield from strings(v, key)
            elif isinstance(value, str) and key != "schema":
                yield value

        texts = list(strings(list(rows.values()))) + list(strings(health)) + [reason]
        for text in texts:
            self.assertNotIn("/", text)
            self.assertNotIn("\\", text)
            self.assertNotIn(self.tmp, text)

    def test_append_cli_and_queries_run(self):
        self.assertEqual(0, self._main("append", "--archive", self.archive, "--run", "2026-10-11_0900_L",
                                       "--run", "2026-10-11_0914_L_a2")[0])
        rc, out = self._main("lane", "L", "--index", self.index)
        self.assertEqual(0, rc)
        self.assertIn("2026-10-11_0914_L_a2", out)
        for argv in (("summary", "--index", self.index), ("lane-table", "--index", self.index),
                     ("trend", "L", "--index", self.index), ("health-regressions", "--index", self.index)):
            rc, out = self._main(*argv)
            self.assertEqual(0, rc, argv)
        rc, out = self._main("lane", "L", "--index", self.index, "--json")
        self.assertEqual("L", json.loads(out)["lane"])

    def test_publish_and_fetch_need_a_branch(self):
        with self.assertRaises(SystemExit):
            with contextlib.redirect_stderr(io.StringIO()):
                self._main("publish", "--index", self.index, "--repo", self.tmp, "--remote", "origin")
        with self.assertRaises(SystemExit):
            with contextlib.redirect_stderr(io.StringIO()):
                self._main("fetch", "--repo", self.tmp, "--remote", "origin", "--out", self.index)


def _have_git():
    try:
        return subprocess.run(["git", "--version"], capture_output=True).returncode == 0
    except OSError:
        return False


@unittest.skipUnless(_have_git(), "git not on PATH")
class GitPublishTests(unittest.TestCase):
    """publish / fetch against a temp bare repo: no checkout, no real remote."""

    BRANCH = "run-index-test"

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-runindex-git-")
        self.bare = os.path.join(self.tmp, "remote.git")
        subprocess.run(["git", "init", "--quiet", "--bare", self.bare], check=True)
        self.a = self._clone("a")
        self.b = self._clone("b")
        self.logs = []

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _clone(self, name):
        path = os.path.join(self.tmp, name)
        subprocess.run(["git", "init", "--quiet", path], check=True)
        subprocess.run(["git", "-C", path, "remote", "add", "origin", self.bare], check=True)
        return path

    def _publish(self, repo, rows, health=None, **kw):
        return run_index.publish(repo, "origin", self.BRANCH, rows, health, delay=0,
                                 log=self.logs.append, **kw)

    def _ls(self, repo, tip, recursive=True, path=None):
        args = ["git", "-C", repo, "ls-tree", "--name-only"] + (["-r"] if recursive else []) + [tip]
        if path:
            args.append(path)
        return subprocess.run(args, capture_output=True, check=True).stdout.decode().split()

    def _subject(self, repo, rev):
        return subprocess.run(["git", "-C", repo, "log", "-1", "--format=%s", rev],
                              capture_output=True, check=True).stdout.decode().strip()

    def _remote_rows(self, repo):
        text, _health, tip = run_index.fetch(repo, "origin", self.BRANCH)
        return runindex.parse_index(text.decode()).rows, tip

    def test_first_publish_creates_an_orphan_with_its_own_readme(self):
        rows = [_row("2026-10-11_0900_L", lane="L")]
        self.assertEqual("created", self._publish(self.a, rows))
        tip = run_index.remote_tip(self.a, "origin", self.BRANCH)
        parents = subprocess.run(["git", "-C", self.a, "rev-list", "--parents", "-n1", tip],
                                 capture_output=True, check=True).stdout.decode().split()
        self.assertEqual([tip], parents)
        self.assertIn(b"# Parsek harness run index", run_index.read_blob(self.a, tip, "flights/README.md"))
        self.assertEqual(["flights"], self._ls(self.a, tip, recursive=False))
        self.assertEqual("flights: 1 runs (1 from this machine, 0 on the branch before)", self._subject(self.a, tip))
        got, _ = self._remote_rows(self.b)
        self.assertEqual(["2026-10-11_0900_L"], [r["runId"] for r in got])
        # No local branch was made (a push may move the remote-tracking ref).
        refs = subprocess.run(["git", "-C", self.a, "for-each-ref", "refs/heads"], capture_output=True).stdout
        self.assertEqual(b"", refs)

    def test_a_second_publish_from_a_diverged_index_unions_and_local_wins(self):
        shared = _row("2026-10-11_0900_L", lane="L")
        self._publish(self.a, [shared, _row("2026-10-11_1000_M", lane="M")])
        mine = dict(shared, verdict="PARSEK-FAIL")
        self.assertEqual("pushed", self._publish(self.b, [mine, _row("2026-10-12_0900_N", lane="N")]))
        got, tip = self._remote_rows(self.a)
        self.assertEqual(["2026-10-11_0900_L", "2026-10-11_1000_M", "2026-10-12_0900_N"],
                         [r["runId"] for r in got])
        self.assertEqual("PARSEK-FAIL", got[0]["verdict"])
        count = subprocess.run(["git", "-C", self.a, "rev-list", "--count", tip],
                               capture_output=True, check=True).stdout.decode().strip()
        self.assertEqual("2", count)

    def test_nothing_changed_pushes_nothing(self):
        rows = [_row("2026-10-11_0900_L", lane="L")]
        self._publish(self.a, rows)
        tip = run_index.remote_tip(self.a, "origin", self.BRANCH)
        self.assertEqual("unchanged", self._publish(self.b, rows))
        self.assertEqual(tip, run_index.remote_tip(self.a, "origin", self.BRANCH))

    def test_a_concurrent_push_is_retried_after_a_refetch(self):
        self._publish(self.a, [_row("2026-10-10_0900_K", lane="K")])
        raced = []

        def race(attempt):
            if attempt == 1:
                raced.append(self._publish(self.b, [_row("2026-10-11_0900_L", lane="L")]))

        result = self._publish(self.a, [_row("2026-10-12_0900_M", lane="M")], before_push=race)
        self.assertEqual("pushed", result)
        self.assertEqual(["pushed"], raced)
        self.assertTrue(any("rejected" in l for l in self.logs), self.logs)
        got, _ = self._remote_rows(self.b)
        self.assertEqual(["2026-10-10_0900_K", "2026-10-11_0900_L", "2026-10-12_0900_M"],
                         [r["runId"] for r in got])

    def test_lane_health_rides_along_and_merges(self):
        mods = runindex.tag_modules(TAG_MAP)
        row = _row("2026-10-11_0900_L", lane="L")
        doc = runindex.build_lane_health({"L": runindex.lane_health_entry(row, {"Flight": [3, 0, 0]}, mods)},
                                         TAG_MAP)
        self._publish(self.a, [row], doc)
        row_m = _row("2026-10-11_1000_M", lane="M")
        doc_m = runindex.build_lane_health({"M": runindex.lane_health_entry(row_m, {"Ghost": [1, 0, 0]}, mods)},
                                           TAG_MAP)
        self._publish(self.b, [row_m], doc_m)
        _text, health, _tip = run_index.fetch(self.a, "origin", self.BRANCH)
        self.assertEqual(["L", "M"], sorted(json.loads(health.decode())["lanes"]))

    def _seed_branch(self):
        """The branch already holds files the tool does not own."""
        seed = os.path.join(self.tmp, "seed")
        subprocess.run(["git", "init", "--quiet", seed], check=True)
        self.foreign = {"README.md": b"# Someone else's readme\r\nkeep me\n",
                        "metrics.jsonl": b'{"m":1}\n{"m":2}\n',
                        "data/deep/notes.txt": b"nested\n",
                        "flights/notes.txt": b"someone's note in the folder\n",
                        "tools/run.sh": b"#!/bin/sh\necho hi\n"}
        for name, data in self.foreign.items():
            path = os.path.join(seed, *name.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as fh:
                fh.write(data)
        ident = ["-c", "user.name=t", "-c", "user.email=t@localhost", "-c", "core.autocrlf=false"]
        subprocess.run(["git", "-C", seed] + ident + ["add", "-A"], check=True)
        subprocess.run(["git", "-C", seed, "update-index", "--chmod=+x", "tools/run.sh"], check=True)
        subprocess.run(["git", "-C", seed] + ident + ["commit", "--quiet", "-m", "seed"], check=True)
        subprocess.run(["git", "-C", seed, "push", "--quiet", self.bare, "HEAD:refs/heads/%s" % self.BRANCH],
                       check=True, capture_output=True)

    def _assert_foreign_intact(self, repo):
        tip = run_index.remote_tip(repo, "origin", self.BRANCH)
        for name, data in self.foreign.items():
            self.assertEqual(data, run_index.read_blob(repo, tip, name), name)
        self.assertEqual(sorted(set(self.foreign) | {"flights/README.md", "flights/index.jsonl"}),
                         sorted(self._ls(repo, tip)))
        mode = subprocess.run(["git", "-C", repo, "ls-tree", tip, "tools/run.sh"],
                              capture_output=True, check=True).stdout.decode().split()[0]
        self.assertEqual("100755", mode)
        return tip

    def test_publish_never_touches_a_file_it_does_not_own(self):
        self._seed_branch()
        self.assertEqual("pushed", self._publish(self.a, [_row("2026-10-10_0900_K", lane="K")]))
        self._assert_foreign_intact(self.a)
        self.assertEqual("pushed", self._publish(self.b, [_row("2026-10-11_0900_L", lane="L")]))
        self._assert_foreign_intact(self.a)
        raced = []

        def race(attempt):
            if attempt == 1:
                raced.append(self._publish(self.b, [_row("2026-10-12_0900_M", lane="M")]))

        self.assertEqual("pushed", self._publish(self.a, [_row("2026-10-13_0900_N", lane="N")],
                                                 before_push=race))
        self.assertEqual(["pushed"], raced)
        self.assertTrue(any("rejected" in l for l in self.logs), self.logs)
        tip = self._assert_foreign_intact(self.b)
        got, _ = self._remote_rows(self.b)
        self.assertEqual(["2026-10-10_0900_K", "2026-10-11_0900_L", "2026-10-12_0900_M", "2026-10-13_0900_N"],
                         [r["runId"] for r in got])
        count = subprocess.run(["git", "-C", self.a, "rev-list", "--count", tip],
                               capture_output=True, check=True).stdout.decode().strip()
        self.assertEqual("5", count)

    def test_commit_files_refuses_a_file_it_does_not_own(self):
        for files, directory in (({"README.md": b"x"}, "flights"), ({"flights/notes.txt": b"x"}, "flights"),
                                 ({"README.md": None}, "flights"), ({"metrics.jsonl": None}, "flights"),
                                 ({"index.jsonl": None}, ""), ({"flights/index.jsonl": b"x"}, "")):
            with self.assertRaises(RuntimeError, msg=(files, directory)):
                run_index._commit_files(self.a, None, files, "m", directory)
        for bad in ("..", "a/../b", "/abs", "c:"):
            with self.assertRaises(RuntimeError, msg=bad):
                run_index.branch_dir(bad if bad != "/abs" else "/abs/..")
        self.assertEqual("flights", run_index.branch_dir("flights/"))
        self.assertEqual("a/b", run_index.branch_dir("a\\b"))
        self.assertEqual("", run_index.branch_dir(""))

    def test_the_root_layout_still_publishes_and_fetch_falls_back_to_it(self):
        self.assertEqual("created", self._publish(self.a, [_row("2026-10-10_0900_K", lane="K")], directory=""))
        tip = run_index.remote_tip(self.a, "origin", self.BRANCH)
        self.assertEqual(["RUN-INDEX.md", "index.jsonl"], self._ls(self.a, tip))
        self.assertTrue(self._subject(self.a, tip).startswith("flights: "))
        for directory in ("", "flights"):
            text, _h, _t = run_index.fetch(self.b, "origin", self.BRANCH, directory)
            self.assertEqual(["2026-10-10_0900_K"], [r["runId"] for r in runindex.parse_index(text.decode()).rows])

    def test_a_publish_moves_the_root_layout_into_the_folder(self):
        self._seed_branch()
        mods = runindex.tag_modules(TAG_MAP)
        k = _row("2026-10-10_0900_K", lane="K")
        doc_k = runindex.build_lane_health({"K": runindex.lane_health_entry(k, {"Flight": [3, 0, 0]}, mods)}, TAG_MAP)
        self._publish(self.a, [k], doc_k, directory="")
        tip = run_index.remote_tip(self.a, "origin", self.BRANCH)
        self.assertTrue({"RUN-INDEX.md", "index.jsonl", "lane-health.json"} <= set(self._ls(self.a, tip)))
        self.assertEqual("pushed", self._publish(self.b, [_row("2026-10-11_0900_L", lane="L")]))
        tip = run_index.remote_tip(self.b, "origin", self.BRANCH)
        listing = self._ls(self.b, tip)
        for gone in ("RUN-INDEX.md", "index.jsonl", "lane-health.json"):
            self.assertNotIn(gone, listing)
        self.assertEqual(sorted(set(self.foreign) | {"flights/README.md", "flights/index.jsonl",
                                                     "flights/lane-health.json"}), sorted(listing))
        for name, data in self.foreign.items():
            self.assertEqual(data, run_index.read_blob(self.b, tip, name), name)
        got, _ = self._remote_rows(self.a)
        self.assertEqual(["2026-10-10_0900_K", "2026-10-11_0900_L"], [r["runId"] for r in got])
        health = json.loads(run_index.read_blob(self.b, tip, "flights/lane-health.json").decode())
        self.assertEqual(["K"], sorted(health["lanes"]))
        self.assertIn("moved RUN-INDEX.md, index.jsonl, lane-health.json from the branch root",
                      self._subject(self.b, tip))
        # The next publish has nothing to move.
        self.assertEqual("unchanged", self._publish(self.a, [k]))

    def test_a_same_named_root_file_of_someone_else_is_never_taken(self):
        seed = os.path.join(self.tmp, "seed")
        subprocess.run(["git", "init", "--quiet", seed], check=True)
        foreign = {"index.jsonl": b'{"x":1}\n', "lane-health.json": b'{"schema":"other"}\n',
                   "RUN-INDEX.md": b"# Not ours\n"}
        for name, data in foreign.items():
            with open(os.path.join(seed, name), "wb") as fh:
                fh.write(data)
        ident = ["-c", "user.name=t", "-c", "user.email=t@localhost"]
        subprocess.run(["git", "-C", seed] + ident + ["add", "-A"], check=True)
        subprocess.run(["git", "-C", seed] + ident + ["commit", "--quiet", "-m", "seed"], check=True)
        subprocess.run(["git", "-C", seed, "push", "--quiet", self.bare, "HEAD:refs/heads/%s" % self.BRANCH],
                       check=True, capture_output=True)
        self.assertEqual("pushed", self._publish(self.a, [_row("2026-10-11_0900_L", lane="L")]))
        tip = run_index.remote_tip(self.a, "origin", self.BRANCH)
        for name, data in foreign.items():
            self.assertEqual(data, run_index.read_blob(self.a, tip, name), name)
        self.assertEqual(1, len(self._remote_rows(self.a)[0]))

    def test_newer_schema_lines_survive_a_publish_from_either_side(self):
        newer_remote = '{"schema":"parsek-run-index/2","from":"remote"}'
        newer_local = '{"schema":"parsek-run-index/2","from":"local"}'
        k = runindex.normalize([_row("2026-10-10_0900_K", lane="K")])
        self._publish(self.a, k, newer=[runindex.NewerLine(runindex.sort_key(k[0]), newer_remote)])
        self._publish(self.b, [_row("2026-10-11_0900_L", lane="L")], newer=[runindex.NewerLine(None, newer_local)])
        text, _h, _tip = run_index.fetch(self.a, "origin", self.BRANCH)
        lines = text.decode().splitlines()
        self.assertEqual(newer_local, lines[0])
        self.assertEqual(newer_remote, lines[2])
        self.assertEqual(2, len(runindex.parse_index(text.decode()).rows))

    def test_fetch_of_an_absent_branch_is_none(self):
        self.assertEqual((None, None, None), run_index.fetch(self.a, "origin", self.BRANCH))

    def test_ci_metrics_append_and_the_flight_publish_share_one_branch(self):
        """Both writers of the metrics branch in alternation, each racing the
        other once: CI's metrics_append.py (checks the tip out, commits only
        metrics.jsonl) never drops flights/, and the flight publish never
        touches metrics.jsonl or the root README.md."""
        sys.path.insert(0, os.path.join(REPO_ROOT, "scripts", "arch"))
        try:
            import metrics_append
        finally:
            sys.path.remove(os.path.join(REPO_ROOT, "scripts", "arch"))
        ci = os.path.join(self.tmp, "ci")
        ident = ["-c", "user.name=t", "-c", "user.email=t@localhost"]
        subprocess.run(["git", "init", "--quiet", ci], check=True)
        subprocess.run(["git", "-C", ci, "remote", "add", "origin", self.bare], check=True)
        with open(os.path.join(ci, "code.txt"), "w") as fh:
            fh.write("code\n")
        subprocess.run(["git", "-C", ci] + ident + ["add", "code.txt"], check=True)
        subprocess.run(["git", "-C", ci] + ident + ["commit", "--quiet", "-m", "code"], check=True)
        works = iter(range(100))

        def metrics(sha, **kw):
            row = {"schema": "parsek-metrics/1", "commit": {"sha": sha * 40, "date": "2026-11-12T00:00:00Z"}}
            return metrics_append.publish(ci, "origin", self.BRANCH,
                                          os.path.join(self.tmp, "w%d" % next(works)), row,
                                          delay=0, log=self.logs.append, **kw)

        self.assertEqual("pushed", metrics("1"))
        self.assertEqual("pushed", self._publish(self.a, [_row("2026-10-10_0900_K", lane="K")]))

        def flight_race(attempt):
            if attempt == 1:
                self._publish(self.b, [_row("2026-10-11_0900_L", lane="L")])

        self.assertEqual("pushed", metrics("2", before_push=flight_race))

        def ci_race(attempt):
            if attempt == 1:
                metrics("3")

        self.assertEqual("pushed", self._publish(self.a, [_row("2026-10-12_0900_M", lane="M")],
                                                 before_push=ci_race))
        rejected = [l for l in self.logs if "rejected" in l]
        self.assertTrue(any(l.startswith("metrics:") for l in rejected), self.logs)
        self.assertTrue(any(l.startswith("run index:") for l in rejected), self.logs)
        tip = run_index.remote_tip(self.a, "origin", self.BRANCH)
        self.assertEqual(metrics_append.README.encode("utf-8"), run_index.read_blob(self.a, tip, "README.md"))
        history = run_index.read_blob(self.a, tip, "metrics.jsonl").decode()
        self.assertEqual(["1" * 40, "2" * 40, "3" * 40], metrics_append.read_shas(history))
        self.assertEqual(["2026-10-10_0900_K", "2026-10-11_0900_L", "2026-10-12_0900_M"],
                         [r["runId"] for r in self._remote_rows(self.b)[0]])
        self.assertEqual(["README.md", "flights/README.md", "flights/index.jsonl", "metrics.jsonl"],
                         self._ls(self.a, tip))

        def subjects(path):
            return subprocess.run(["git", "-C", self.a, "log", "--format=%s", tip, "--", path],
                                  capture_output=True, check=True).stdout.decode().splitlines()

        flights = subjects("flights/")
        self.assertEqual(3, len(flights))
        self.assertTrue(all(s.startswith("flights: ") for s in flights), flights)
        ci_rows = subjects("metrics.jsonl")
        self.assertEqual(3, len(ci_rows))
        self.assertTrue(all(s.startswith("metrics: ") for s in ci_rows), ci_rows)


# ---------------------------------------------------------------------------
# run.py appends to the index after it archives (verdict-neutral)
# ---------------------------------------------------------------------------

class FakeRunIndexSmokeTests(unittest.TestCase):
    """A fake-KSP run through the production retry loop appends to the index
    of the TEMP umbrella it was handed; an index failure is one Warn line and
    the verdict stands; the archive opt-out skips the index too."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-runindex-smoke-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "2026-10-11_090000_harness.log"))
        self.archive = run.results_archive_root(self.tmp)
        self.index = os.path.join(self.archive, runindex.INDEX_FILE_NAME)
        env = dict(os.environ)
        env.pop(hlib.RESULTS_ARCHIVE_OPT_OUT_ENV, None)
        self._env = mock.patch.dict(os.environ, env, clear=True)
        self._env.start()

    def tearDown(self):
        self._env.stop()
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _fly(self):
        spec = _make_spec(self.template, 30, 600)
        with contextlib.redirect_stdout(io.StringIO()):
            return run._run_scenario_with_retry(spec, self.instance, self.tmp, FakeRuntime("pass"), self.logger)

    def _log(self):
        with open(self.logger.log_path, encoding="utf-8") as fh:
            return fh.read()

    def test_a_fake_run_is_appended_with_health(self):
        result = self._fly()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        parsed = run_index.load_index(self.index)
        rows = parsed.rows
        self.assertEqual([], parsed.dropped)
        self.assertEqual([result["runId"]], [r["runId"] for r in rows])
        row = rows[0]
        self.assertEqual("PASS", row["verdict"])
        self.assertEqual("SMOKE-fake", row["lane"])
        self.assertEqual(os.path.basename(run.WORKTREE_ROOT), row["worktree"])
        self.assertIsNotNone(row["health"])
        self.assertGreater(row["health"]["parsek"], 0)
        self.assertLess(len(runindex.row_line(row)), 600)
        self.assertIn("[Info][Archive] run index", self._log())
        self.assertIn("added=1", self._log())

    def test_a_real_retry_links_although_each_attempt_has_its_own_stamp(self):
        """B1 end to end: run.py stamps every attempt from "now", so the retry's
        run id has another minute than attempt 1; the index still pairs them."""
        launches = []

        class Crash1(FakeRuntime):
            def launch(self, exe, args, env, cwd):
                self.mode = "bootcrash" if not launches else "pass"
                launches.append(self.mode)
                return super().launch(exe, args, env, cwd)

        with mock.patch.object(run, "_run_id_stamp",
                               lambda: "2026-10-11_0914" if launches else "2026-10-11_0900"):
            spec = _make_spec(self.template, 30, 600)
            with contextlib.redirect_stdout(io.StringIO()):
                result = run._run_scenario_with_retry(spec, self.instance, self.tmp, Crash1("pass"),
                                                      self.logger)
        self.assertEqual(["bootcrash", "pass"], launches)
        self.assertEqual(("PASS", "flakedThenPassed"), (result["verdict"], result["note"]))
        rows = run_index.load_index(self.index).rows
        self.assertEqual(["2026-10-11_0900_SMOKE-fake", "2026-10-11_0914_SMOKE-fake_a2"],
                         [r["runId"] for r in rows])
        self.assertEqual(rows[0]["runId"], rows[1]["retryOf"])
        self.assertEqual([False, True], [r["final"] for r in rows])
        self.assertTrue(rows[1]["byRetry"])
        c = runindex.lane_report(rows, "SMOKE-fake")["windows"]["all"]
        self.assertEqual((1, 1, 1), (c["scenarioRuns"], c["finalPasses"], c["flaked"]))

    def test_an_index_failure_keeps_the_verdict(self):
        with mock.patch.object(run_index, "append_runs", side_effect=OSError("disk full")):
            result = self._fly()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        with open(os.path.join(run.RESULTS_DIR, result["runId"] + ".json")) as fh:
            self.assertEqual(hlib.VERDICT_PASS, json.load(fh)["verdict"])
        self.assertTrue(os.path.isdir(os.path.join(self.archive, result["runId"])))
        self.assertIn("[Warn][Archive] run index append FAILED", self._log())
        self.assertIn("verdict unaffected", self._log())

    def test_an_unloadable_tool_keeps_the_verdict(self):
        with mock.patch.object(run, "_load_run_index_module", side_effect=ImportError("gone")):
            result = self._fly()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertIn("run index append FAILED", self._log())

    def test_the_opt_out_env_skips_the_index(self):
        os.environ[hlib.RESULTS_ARCHIVE_OPT_OUT_ENV] = "1"
        result = self._fly()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertFalse(os.path.exists(self.index))
        self.assertNotIn("run index", self._log())

    def test_run_py_and_the_tests_share_one_module_object(self):
        self.assertIs(run_index, run._load_run_index_module())


# ---------------------------------------------------------------------------
# snapshot imports (collect-logs folders of past non-PASS runs)
# ---------------------------------------------------------------------------

SNAP_UTC = 1791709380  # 2026-10-11T09:03:00Z
SNAP_STAMP = "2026-10-11_0903"
SNAP_LANE = "B11-mun-orbit"
SNAP_DLL = "f8f465a6" * 8
HARNESS_LOG_PATH = r"C:\Users\someone\Dev\automation\stock-minimal\KSP.log"
DEV_LOG_PATH = r"C:\Users\someone\Dev\Kerbal Space Program\KSP.log"
GIT_DIRTY = ("Branch: some-branch\nCommit: 0123456789abcdef Fix a thing (#12)\n\n--- status ---\n"
             " M harness/run.py\n\n--- diff --stat ---\n harness/run.py | 2 +-\n")
GIT_CLEAN = "Branch: main\nCommit: fedcba9876543 Merge\n\n--- status ---\n\n\n--- diff --stat ---\n\n"


def _epoch(stamp):
    """``2026-10-11_0903`` (UTC) -> epoch seconds."""
    return int(calendar.timegm(time.strptime(stamp, "%Y-%m-%d_%H%M")))


def _snap_log(run_utc=SNAP_UTC, armed=True, extra=(), dll=True):
    lines = [b"[LOG 09:03:00.100] Log Initiated\n",
             _pline("INFO", "Init", "SessionStart runUtc=%d" % run_utc) if run_utc is not None else b""]
    if dll:
        lines.append(("[LOG 09:03:01.000] [KSPCommunityFixes] Parsek 0.10.5.0 Parsek.dll %s\n"
                      % SNAP_DLL).encode("ascii"))
    if armed:
        lines.append(_pline("INFO", "TestCommands", "armed (PARSEK_TEST_COMMANDS=1)"))
    lines += [_pline("WARN", "Ghost", "w"), _pline("INFO", "Flight", "hello")]
    lines += list(extra)
    return b"".join(lines)


def _snap_folder(logs, name, log=None, log_path=HARNESS_LOG_PATH, git=GIT_DIRTY):
    d = os.path.join(logs, name)
    os.makedirs(d)
    if log is not False:
        with open(os.path.join(d, "KSP.log"), "wb") as fh:
            fh.write(_snap_log() if log is None else log)
    if log_path is not None:
        with open(os.path.join(d, "log-validation.txt"), "w", encoding="utf-8") as fh:
            fh.write("KSP.log validation: FAILED\nLog path: %s\n\nsome output\n" % log_path)
    if git is not None:
        with open(os.path.join(d, "git-state.txt"), "w", encoding="utf-8") as fh:
            fh.write(git)
    return d


def _facts(data):
    f = runindex.SnapshotLogFacts()
    for line in io.BytesIO(data):
        f.feed(line)
    return f


def _decide(folder="2026-10-11_1203_" + SNAP_LANE, log=None, validation=None, git=GIT_DIRTY,
            state=None, seen=None):
    return runindex.decide_snapshot(
        folder, True, "Log path: %s\n" % (validation or HARNESS_LOG_PATH), git,
        _facts(_snap_log() if log is None else log), state or runindex.ArchiveState(),
        set() if seen is None else seen)


def _snap_row(stamp, lane, run_utc, health=None, **marker):
    m = {"schema": runindex.SNAPSHOT_SCHEMA, "lane": lane, "runUtc": run_utc,
         "commit": "0123456789ab", "profile": "stock-minimal"}
    m.update(marker)
    row, reason = runindex.build_snapshot_row("%s_%s" % (stamp, lane), m, health)
    assert reason is None, reason
    return row


class SnapshotDecisionTests(unittest.TestCase):
    def test_a_harness_folder_imports_with_a_utc_run_id_and_a_path_free_marker(self):
        # The folder name's 12:03 is LOCAL time; the run id and start come from runUtc.
        d = _decide()
        self.assertEqual(("import", "imported"), (d.outcome, d.reason))
        self.assertEqual("%s_%s" % (SNAP_STAMP, SNAP_LANE), d.run_id)
        m = d.marker
        self.assertEqual({"schema": runindex.SNAPSHOT_SCHEMA, "runId": d.run_id, "lane": SNAP_LANE,
                          "runUtc": SNAP_UTC, "started": "2026-10-11T09:03:00Z", "commit": "0123456789ab",
                          "dirty": True, "profile": "stock-minimal", "dll": SNAP_DLL,
                          "source": "2026-10-11_1203_" + SNAP_LANE,
                          "tool": "harness/tools/run_index.py import-snapshots"}, m)
        text = runindex.serialize_marker(m)
        for bad in ("\\", "Users", "someone", "Dev"):
            self.assertNotIn(bad, text)

    def test_a_dev_snapshot_is_skipped(self):
        d = _decide(validation=DEV_LOG_PATH)
        self.assertEqual(("skip", "dev-snapshot"), (d.outcome, d.reason))

    def test_classify_log_path(self):
        c = runindex.classify_log_path
        self.assertEqual(("harness", "stock-minimal"), c("Log path: %s" % HARNESS_LOG_PATH))
        self.assertEqual(("harness", "kitchen-sink"), c("x\nLog path: /home/u/automation/kitchen-sink/KSP.log\n"))
        self.assertEqual(("dev", None), c("Log path: %s" % DEV_LOG_PATH))
        self.assertEqual(("unknown", None), c("Log path: D:\\elsewhere\\KSP.log"))
        self.assertEqual(("unknown", None), c("Log path: C:\\automation\\bad name\\KSP.log"))
        self.assertEqual(("unknown", None), c(None))

    def test_no_instance_path_or_no_arm_line_is_skipped(self):
        d = _decide(validation=r"D:\somewhere\KSP.log")
        self.assertEqual("no-instance-log-path", d.reason)
        d = _decide(log=_snap_log(armed=False))
        self.assertEqual("not-armed", d.reason)

    def test_no_run_utc_or_two_sessions_is_skipped(self):
        self.assertEqual("no-run-utc", _decide(log=_snap_log(run_utc=None)).reason)
        two = _snap_log(extra=[_pline("INFO", "Init", "SessionStart runUtc=%d" % (SNAP_UTC + 600))])
        self.assertEqual("several-sessions", _decide(log=two).reason)
        # The same line twice (one session) is one session.
        same = _snap_log(extra=[_pline("INFO", "Init", "SessionStart runUtc=%d" % SNAP_UTC)])
        self.assertEqual("imported", _decide(log=same).reason)

    def test_a_label_that_is_not_a_token_is_skipped(self):
        for folder in ("2026-10-11_1203_bad label", "2026-10-11_1203_x/y", "2026-10-11_1203_-dash"):
            self.assertEqual(("skip", "label-not-a-token"), (_decide(folder).outcome, _decide(folder).reason))
        self.assertEqual("not-a-snapshot-folder", _decide("notes").reason)

    def test_already_archived_at_the_same_minute_or_the_minute_before(self):
        for archived in ("%s_%s" % (SNAP_STAMP, SNAP_LANE), "2026-10-11_0902_%s" % SNAP_LANE,
                         "2026-10-11_0902_%s_run2" % SNAP_LANE, "2026-10-11_0903_%s_a2" % SNAP_LANE):
            state = runindex.ArchiveState()
            state.add_run(archived)
            d = _decide(state=state)
            self.assertEqual(("skip", "already-archived", archived), (d.outcome, d.reason, d.run_id), archived)
        for other in ("2026-10-11_0901_%s" % SNAP_LANE, "2026-10-11_0904_%s" % SNAP_LANE,
                      "%s_OTHER-1" % SNAP_STAMP):
            state = runindex.ArchiveState()
            state.add_run(other)
            self.assertEqual("imported", _decide(state=state).reason, other)

    def test_a_taken_run_id_gets_an_ordinal(self):
        state = runindex.ArchiveState()
        state.add_snapshot("%s_%s" % (SNAP_STAMP, SNAP_LANE), SNAP_LANE, SNAP_UTC - 30)
        d = _decide(log=_snap_log(run_utc=SNAP_UTC + 20), state=state)
        self.assertEqual("%s_%s_run2" % (SNAP_STAMP, SNAP_LANE), d.run_id)

    def test_a_duplicate_pair_imports_once(self):
        state, seen = runindex.ArchiveState(), set()
        first = _decide("2026-10-11_1203_" + SNAP_LANE, state=state, seen=seen)
        second = _decide("2026-10-11_1204_" + SNAP_LANE, state=state, seen=seen)
        self.assertEqual(("imported", "duplicate"), (first.reason, second.reason))
        # Another lane at the same boot second is not a duplicate.
        third = _decide("2026-10-11_1204_OTHER-1", state=state, seen=seen)
        self.assertEqual("imported", third.reason)

    def test_an_imported_snapshot_is_not_imported_again(self):
        state = runindex.ArchiveState()
        state.add_snapshot("%s_%s" % (SNAP_STAMP, SNAP_LANE), SNAP_LANE, SNAP_UTC)
        d = _decide(state=state)
        self.assertEqual(("skip", "already-imported", "%s_%s" % (SNAP_STAMP, SNAP_LANE)),
                         (d.outcome, d.reason, d.run_id))

    def test_git_state(self):
        self.assertEqual(("0123456789ab", True), runindex.parse_git_state(GIT_DIRTY))
        self.assertEqual(("fedcba9876543"[:12], False), runindex.parse_git_state(GIT_CLEAN))
        self.assertEqual((None, None), runindex.parse_git_state("(git not found)"))
        self.assertEqual((None, None), runindex.parse_git_state(None))

    def test_the_dll_is_taken_only_from_an_unambiguous_line(self):
        self.assertEqual([SNAP_DLL], _facts(_snap_log()).dlls)
        self.assertEqual([], _facts(_snap_log(dll=False)).dlls)
        noisy = [_pline("INFO", "Init", "Parsek 0.10.5.0 hash %s" % ("a" * 64)),
                 ("Parsek 0.10.5.0 %s %s\n" % ("b" * 64, "c" * 64)).encode("ascii"),
                 ("ParsekX 0.10.5.0 %s\n" % ("d" * 64)).encode("ascii")]
        f = _facts(b"".join(noisy))
        self.assertEqual([], f.dlls)
        two = _snap_log(extra=[("Parsek 0.10.6.0 %s\n" % ("e" * 64)).encode("ascii")])
        self.assertEqual(2, len(_facts(two).dlls))
        self.assertIsNone(_decide(log=two).marker["dll"])

    def test_the_dll_is_taken_from_the_padded_mod_table_line(self):
        # The KSPCommunityFixes table pads the name column (as seen on the PC).
        line = ("  Parsek" + " " * 38 + "0.10.5.0   0.10.5.0   GameData/Parsek/Plugins   "
                + SNAP_DLL + "\n").encode("ascii")
        self.assertEqual([SNAP_DLL], _facts(line).dlls)
        tab = ("[LOG 09:03:01.000]   Parsek\t\t0.10.5.0 %s\n" % SNAP_DLL).encode("ascii")
        self.assertEqual([SNAP_DLL], _facts(tab).dlls)
        other = ("  ParsekTools" + " " * 33 + "0.10.5.0 " + SNAP_DLL + "\n").encode("ascii")
        self.assertEqual([], _facts(other).dlls)
        no_hash = ("  Parsek" + " " * 38 + "0.10.5.0   0.10.5.0\n").encode("ascii")
        self.assertEqual([], _facts(no_hash).dlls)
        log = _snap_log(dll=False, extra=[line])
        self.assertEqual(SNAP_DLL, _decide(log=log).marker["dll"])

    def test_a_non_utf8_log_reads(self):
        log = _snap_log(extra=[b"[LOG 09:04:00.000] \xff\xfe\x80 garbage \xc3\x28\n"])
        self.assertEqual("imported", _decide(log=log).reason)


class SnapshotRowTests(unittest.TestCase):
    def test_the_row_shape(self):
        row = _snap_row(SNAP_STAMP, SNAP_LANE, SNAP_UTC, _health(), dirty=True, dll=SNAP_DLL,
                        source="2026-10-11_1203_" + SNAP_LANE)
        self.assertEqual({"schema": runindex.SCHEMA, "runId": "%s_%s" % (SNAP_STAMP, SNAP_LANE),
                          "lane": SNAP_LANE, "tier": None, "profile": "stock-minimal",
                          "started": "2026-10-11T09:03:00Z", "wall": None, "attempt": 1,
                          "verdict": runindex.SNAPSHOT_VERDICT, "commit": "0123456789ab",
                          "dll": SNAP_DLL[:16], "worktree": None, "origin": runindex.SNAPSHOT_ORIGIN,
                          "health": _health(), "final": False, "dirty": True}, row)
        self.assertNotIn(row["verdict"], (hlib.VERDICT_PASS, hlib.VERDICT_INVALID, hlib.VERDICT_PARSEK_FAIL,
                                          hlib.VERDICT_KILLED, hlib.VERDICT_XPASS))
        parsed = runindex.parse_index(runindex.serialize_index([row]))
        self.assertEqual(([], []), (parsed.dropped, parsed.newer))
        self.assertEqual(1, len(parsed.rows))

    def test_bad_markers_are_refused_and_bad_strings_withheld(self):
        good = {"schema": runindex.SNAPSHOT_SCHEMA, "lane": "L", "runUtc": SNAP_UTC}
        self.assertIsNone(runindex.build_snapshot_row("2026-10-11_0903_L", dict(good, schema="x"), None)[0])
        self.assertIsNone(runindex.build_snapshot_row("2026-10-11_0903_L", dict(good, lane="a b"), None)[0])
        self.assertIsNone(runindex.build_snapshot_row("2026-10-11_0903_L", dict(good, runUtc="1"), None)[0])
        self.assertIsNone(runindex.build_snapshot_row("../x", good, None)[0])
        row, _r = runindex.build_snapshot_row("2026-10-11_0903_L", dict(
            good, profile=r"C:\Users\x", commit="not hex"), None)
        self.assertEqual((None, None), (row["profile"], row["commit"]))
        self.assertEqual(["commit withheld", "profile withheld"], row["gaps"])
        self.assertNotIn("dirty", row)


class SnapshotQueryTests(unittest.TestCase):
    """Snapshot rows show in lane history and trend and are counted apart;
    no count, rate, link, last-not-PASS, wall percentile or regression reads them."""

    def setUp(self):
        self.harness = [
            _attempt("2026-10-20_0900", "L", "INVALID", subkind="boot-crash", wall=50),
            _attempt("2026-10-20_0912", "L", "PASS", 2, wall=100, health=_health()),
            _attempt("2026-10-21_0900", "L", "PASS", wall=110, health=_health()),
            _attempt("2026-10-22_0900", "L", "PASS", wall=120, health=_health()),
            _attempt("2026-10-23_0900", "L", "PASS", wall=100, health=_health()),
            _attempt("2026-10-24_0900", "L", "PASS", wall=100, health=_health()),
            _attempt("2026-10-20_1000", "M", "PASS", wall=10),
        ]
        big = _health(warn=900, error=900, parsek=99999, nbytes=10 ** 9)
        self.snaps = [
            _snap_row("2026-09-01_0900", "L", _epoch("2026-09-01_0900"), big),
            _snap_row("2026-10-25_0900", "L", _epoch("2026-10-25_0900"), big),  # newer than every harness row
            _snap_row("2026-10-26_0900", "M", _epoch("2026-10-26_0900"), big),
            _snap_row("2026-09-02_0900", "OLD-1", _epoch("2026-09-02_0900"), big),  # a deleted lane
        ]
        self.rows = runindex.normalize(self.harness + self.snaps)
        self.base = runindex.normalize(self.harness)

    def test_snapshot_rows_are_never_final_and_never_linked(self):
        by_id = {r["runId"]: r for r in self.rows}
        for s in self.snaps:
            self.assertFalse(by_id[s["runId"]]["final"])
            self.assertNotIn("retryOf", by_id[s["runId"]])
            self.assertNotIn("byRetry", by_id[s["runId"]])
        self.assertEqual({"2026-10-20_0912_L_a2": "2026-10-20_0900_L"}, runindex.link_attempts(self.rows))
        # Even a snapshot row that looked like a retryable attempt 1 claims no retry.
        retry = _attempt("2026-10-24_0905", "L", "PASS", 2)
        fake = dict(self.snaps[1], verdict=hlib.VERDICT_INVALID, subkind="boot-crash",
                    started="2026-10-24T08:50:00Z", runId="2026-10-24_0850_L", worktree=retry["worktree"])
        self.assertEqual({}, runindex.link_attempts([fake, retry]))
        self.assertTrue(runindex.retryable(fake))

    def test_lane_report_counts_snapshots_apart(self):
        rep = runindex.lane_report(self.rows, "L", "2026-10-26T09:00:00Z")
        base = runindex.lane_report(self.base, "L", "2026-10-26T09:00:00Z")
        for name in ("30d", "90d", "all"):
            got = dict(rep["windows"][name])
            want = dict(base["windows"][name])
            self.assertEqual(want.pop("snapshots"), 0)
            self.assertEqual({"30d": 1, "90d": 2, "all": 2}[name], got.pop("snapshots"))
            self.assertEqual(want, got, name)
        for key in ("lastRun", "lastPass", "wallP50", "wallP90", "lastWalls", "tier"):
            self.assertEqual(base[key], rep[key], key)
        self.assertEqual("2026-10-25_0900_L", rep["lastSnapshot"]["runId"])
        self.assertIsNone(base["lastSnapshot"])
        only = runindex.lane_report(self.rows, "OLD-1")
        self.assertIsNone(only["lastRun"])
        self.assertEqual("2026-09-02_0900_OLD-1", only["lastSnapshot"]["runId"])

    def test_summary_counts_snapshots_apart(self):
        tiers = {"L": "daily", "M": "daily", "N": "daily"}
        s = runindex.summary(self.rows, tiers)
        base = runindex.summary(self.base, tiers)
        self.assertEqual({"rows": 4, "lanes": 3, "lanesOnlyInSnapshots": ["OLD-1"]}, s.pop("snapshots"))
        self.assertEqual({"rows": 0, "lanes": 0, "lanesOnlyInSnapshots": []}, base.pop("snapshots"))
        self.assertEqual(["OLD-1"], s.pop("lanesWithNoSpec"))
        self.assertEqual([], base.pop("lanesWithNoSpec"))
        self.assertEqual(base, s)
        self.assertEqual([], s["lastNotPass"])
        self.assertEqual(["N"], s["specsWithNoRun"])

    def test_health_regressions_and_latest_pass_ignore_snapshots(self):
        self.assertEqual(runindex.health_regressions(self.base), runindex.health_regressions(self.rows))
        self.assertEqual(runindex.latest_pass_rows(self.base), runindex.latest_pass_rows(self.rows))
        # Even a snapshot row that claimed PASS would not be read as the latest PASS.
        fake = dict(self.snaps[1], verdict=hlib.VERDICT_PASS)
        self.assertEqual([], runindex.health_regressions(self.harness + [fake]))
        self.assertEqual("2026-10-24_0900_L", runindex.latest_pass_rows(self.harness + [fake])["L"]["runId"])

    def test_lane_table_and_trend_show_snapshots(self):
        table = runindex.lane_table(self.rows).splitlines()
        self.assertTrue(table[0].endswith("| Snapshots |"))
        self.assertIn("| L | daily | PASS | 2026-10-24, aaaaaaaaa | 5 | 100% | 100 | 2 |", table)
        self.assertIn("| OLD-1 |  | (snapshots only) | | 0 | | | 1 |", table)
        base = runindex.lane_table(self.base).splitlines()
        self.assertIn("| L | daily | PASS | 2026-10-24, aaaaaaaaa | 5 | 100% | 100 | 0 |", base)
        t = runindex.trend(self.rows, "L", 3)
        self.assertEqual(["2026-10-23_0900_L", "2026-10-24_0900_L", "2026-10-25_0900_L"],
                         [p["runId"] for p in t])
        self.assertEqual(["run", "run", "snapshot"], [p["origin"] for p in t])


class SnapshotSourceSyncTests(unittest.TestCase):
    """The import keys on two C# log lines and on collect-logs.py's layout."""

    def _source(self, rel):
        with open(os.path.join(REPO_ROOT, rel), "r", encoding="utf-8") as fh:
            return fh.read()

    def test_session_start_is_the_init_line_parsekharmony_writes(self):
        text = self._source("Source/Parsek/ParsekHarmony.cs")
        self.assertIn('"SessionStart runUtc=" + runUtcSeconds.ToString(CultureInfo.InvariantCulture)', text)
        self.assertRegex(text, r'ParsekLog\.Info\("Init",\s*FormatSessionStartMessage\(DateTimeOffset\.UtcNow\.ToUnixTimeSeconds\(\)\)\)')
        f = _facts(_pline("INFO", "Init", "SessionStart runUtc=%d" % SNAP_UTC))
        self.assertEqual([SNAP_UTC], f.run_utcs)

    def test_the_arm_line_is_the_one_the_seam_addon_writes(self):
        text = self._source("Source/Parsek/TestCommands/ParsekTestCommandAddon.cs")
        self.assertIn('private const string Tag = "TestCommands";', text)
        self.assertIn('ParsekLog.Info(Tag, "armed (PARSEK_TEST_COMMANDS=1)")', text)
        self.assertTrue(_facts(_pline("INFO", "TestCommands", "armed (PARSEK_TEST_COMMANDS=1)")).armed)

    def test_collect_logs_writes_the_layout_the_import_reads(self):
        text = self._source("scripts/collect-logs.py")
        self.assertIn('datetime.now().strftime("%Y-%m-%d_%H%M")', text)
        self.assertIn('folder = f"{timestamp}_{args.label}"', text)
        self.assertIn('f"Log path: {ksp_log_path}"', text)
        self.assertIn('lines.append(f"Commit: {commit.stdout.strip()}")', text)
        self.assertIn('["git", "log", "--oneline", "-1"]', text)
        self.assertIn('lines.append("--- status ---")', text)
        self.assertIn('["git", "status", "--short"]', text)
        for name in (run_index.LOG_VALIDATION_NAME, run_index.GIT_STATE_NAME):
            self.assertIn('"%s"' % name, text)
        # The harness labels a snapshot with the scenario id.
        self.assertIn('label, "--save", save_name, "--ksp-dir", instance_dir]', self._source("harness/run.py"))


def _outcome_counts(out):
    """{(outcome, reason): n} from import-snapshots' count lines."""
    found = re.findall(r"^  (import|skip)\s+(\S.*?)\s+(\d+)$", out, re.M)
    return {(o, r): int(n) for o, r, n in found}


class _Logger:
    def __init__(self):
        self.lines = []

    def info(self, tag, msg):
        self.lines.append(msg)

    warn = info


class SnapshotImportToolTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-runindex-snap-")
        self.logs = os.path.join(self.tmp, "logs")
        self.archive = os.path.join(self.tmp, "results-archive")
        os.makedirs(self.logs)
        os.makedirs(self.archive)
        self.lane_rid = "%s_%s" % (SNAP_STAMP, SNAP_LANE)
        # harness, imported
        _snap_folder(self.logs, "2026-10-11_1203_" + SNAP_LANE)
        # its duplicate (collected twice)
        _snap_folder(self.logs, "2026-10-11_1204_" + SNAP_LANE)
        # dev snapshot
        _snap_folder(self.logs, "2026-10-11_1300_my-check", log_path=DEV_LOG_PATH)
        # no runUtc
        _snap_folder(self.logs, "2026-10-11_1301_X-1", log=_snap_log(run_utc=None))
        # non-token label
        _snap_folder(self.logs, "2026-10-11_1302_bad label")
        # non-UTF-8 log, clean tree, other lane
        _snap_folder(self.logs, "2026-10-12_0800_Y-1", git=GIT_CLEAN, log=_snap_log(
            run_utc=SNAP_UTC + 86400, extra=[b"\xff\xfe\x80 \xc3\x28 broken\n"]))
        # archived by run.py at the minute before the boot
        _write_run(self.archive, "2026-10-12_0902_Z-1", _result("2026-10-12_0902_Z-1", "INVALID", lane="Z-1",
                                                                  started="2026-10-12T09:02:00Z"))
        _snap_folder(self.logs, "2026-10-12_1203_Z-1", log=_snap_log(run_utc=SNAP_UTC + 86400))
        # no KSP.log; a stray file
        _snap_folder(self.logs, "2026-10-12_1204_W-1", log=False)
        with open(os.path.join(self.logs, "README.txt"), "w") as fh:
            fh.write("x")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _main(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = run_index.main(list(argv))
        return rc, out.getvalue()

    def _tree(self):
        out = {}
        for root, _dirs, files in os.walk(self.archive):
            for f in files:
                p = os.path.join(root, f)
                with open(p, "rb") as fh:
                    out[os.path.relpath(p, self.archive)] = (fh.read(), os.stat(p).st_mtime_ns)
        return out

    def _import(self, *extra):
        return self._main("import-snapshots", "--from", self.logs, "--archive", self.archive, *extra)

    def test_dry_run_counts_by_outcome_and_reason_and_writes_nothing(self):
        before = self._tree()
        rc, out = self._import("--dry-run", "--list")
        self.assertEqual(0, rc, out)
        self.assertEqual(before, self._tree())
        self.assertEqual({("import", "imported"): 2, ("skip", "already-archived"): 1,
                          ("skip", "dev-snapshot"): 1, ("skip", "duplicate"): 1,
                          ("skip", "label-not-a-token"): 1, ("skip", "no-ksp-log"): 1,
                          ("skip", "no-run-utc"): 1}, _outcome_counts(out))
        self.assertIn("8 folder(s)", out)
        self.assertIn("imports: commit 2, dirty 1, dll 2, profile 2", out)
        self.assertIn("matched (a PASS means the harness did collect logs for a passing run): INVALID 1", out)
        self.assertIn("2026-10-12_1203_Z-1 -> skip already-archived 2026-10-12_0902_Z-1", out)

    def test_import_writes_the_log_and_marker_only_and_is_idempotent(self):
        rc, out = self._import()
        self.assertEqual(0, rc, out)
        d = os.path.join(self.archive, self.lane_rid)
        self.assertEqual(["KSP.log.gz", "snapshot.json"], sorted(os.listdir(d)))
        with gzip.open(os.path.join(d, "KSP.log.gz"), "rb") as fh:
            self.assertEqual(_snap_log(), fh.read())
        with open(os.path.join(d, "snapshot.json")) as fh:
            marker = json.load(fh)
        self.assertEqual("2026-10-11_1203_" + SNAP_LANE, marker["source"])
        y = "2026-10-12_0903_Y-1"
        self.assertTrue(os.path.isfile(os.path.join(self.archive, y, "snapshot.json")))
        self.assertEqual(sorted([self.lane_rid, y, "2026-10-12_0902_Z-1"]),
                         sorted(n for n in os.listdir(self.archive)))
        first = self._tree()
        rc, out = self._import()
        self.assertEqual(0, rc, out)
        # Both copies of the doubled snapshot and the other import.
        self.assertEqual(3, _outcome_counts(out)[("skip", "already-imported")])
        self.assertIn("0 imported", out)
        self.assertEqual(first, self._tree())
        for name, (data, _m) in first.items():
            if name.endswith("snapshot.json"):
                self.assertNotIn(self.tmp.encode(), data)
                self.assertNotIn(b"someone", data)

    def test_an_existing_folder_is_never_overwritten(self):
        os.makedirs(os.path.join(self.archive, self.lane_rid))
        with open(os.path.join(self.archive, self.lane_rid, "keep.txt"), "w") as fh:
            fh.write("mine")
        rc, out = self._import("--list")
        self.assertIn("2026-10-11_1203_%s -> skip already-archived %s" % (SNAP_LANE, self.lane_rid), out)
        self.assertEqual(["keep.txt"], os.listdir(os.path.join(self.archive, self.lane_rid)))
        with self.assertRaises(FileExistsError):
            run_index.write_snapshot_folder(self.archive, self.lane_rid,
                                            os.path.join(self.logs, "2026-10-11_1203_" + SNAP_LANE, "KSP.log"),
                                            {"schema": runindex.SNAPSHOT_SCHEMA})
        self.assertEqual(["keep.txt"], os.listdir(os.path.join(self.archive, self.lane_rid)))

    def test_rebuild_equals_import_then_append(self):
        rc, out = self._import("--append")
        self.assertEqual(0, rc, out)
        self.assertIn("run index: added=2", out)
        appended = run_index.load_index(os.path.join(self.archive, "index.jsonl")).rows
        rebuilt_path = os.path.join(self.tmp, "rebuilt.jsonl")
        rc, out = self._main("rebuild", "--archive", self.archive, "--out", rebuilt_path)
        self.assertEqual(0, rc, out)
        rebuilt = run_index.load_index(rebuilt_path).rows
        snaps = [r for r in rebuilt if r.get("origin") == "snapshot"]
        self.assertEqual(sorted([self.lane_rid, "2026-10-12_0903_Y-1"]), sorted(r["runId"] for r in snaps))
        self.assertEqual(runindex.normalize([r for r in rebuilt if r.get("origin") == "snapshot"]),
                         runindex.normalize(appended))
        row = {r["runId"]: r for r in snaps}[self.lane_rid]
        self.assertEqual(("NOT-PASS", 1, False, None), (row["verdict"], row["attempt"], row["final"], row["wall"]))
        self.assertEqual((1, 4), (row["health"]["warn"], row["health"]["parsek"]))
        y = {r["runId"]: r for r in snaps}["2026-10-12_0903_Y-1"]
        self.assertNotIn("dirty", y)
        self.assertTrue(y["health"]["bytes"] > 0)
        with open(rebuilt_path) as fh:
            text = fh.read()
        self.assertNotIn(self.tmp, text)
        self.assertNotIn("someone", text)
        rc, out = self._main("lane", "2026-10-11_1203_x", "--index", rebuilt_path)
        self.assertEqual(1, rc)
        rc, out = self._main("lane", SNAP_LANE, "--index", rebuilt_path)
        self.assertEqual(0, rc, out)
        self.assertIn("last snapshot (not a PASS", out)

    def test_every_other_archive_reader_tolerates_a_snapshot_folder(self):
        self._import()
        snap_dir = os.path.join(self.archive, self.lane_rid)
        import flight_efficiency
        import mutation_check
        import archive_results
        # flight_efficiency: snapshot.json's stem is no run id, so no candidate.
        self.assertEqual(["2026-10-12_0902_Z-1"], [c["runId"] for c in flight_efficiency.discover_archive(self.archive)])
        # mutation_check: no plain KSP.log and no stamped result JSON, so no ref.
        self.assertEqual([], [r for r in mutation_check.discover([self.archive, snap_dir])
                              if r.run_id == self.lane_rid])
        # run_index's PASS pre-pass reads result JSONs only.
        self.assertEqual(["2026-10-12_0902_Z-1"],
                         [r["runId"] for r in run_index._cheap_rows(self.archive, os.listdir(self.archive))])
        # archive_results back-fill leaves an existing run folder alone.
        results = os.path.join(self.tmp, "results")
        os.makedirs(results)
        with open(os.path.join(results, self.lane_rid + ".json"), "w") as fh:
            json.dump(_result(self.lane_rid, "INVALID", lane=SNAP_LANE), fh)
        before = self._tree()
        counts = archive_results.backfill(results, self.archive, _Logger())
        self.assertEqual(1, counts["existing"])
        self.assertEqual(before, self._tree())

    def test_an_unreadable_folder_costs_only_itself(self):
        real = run_index.snapshot_facts

        def boom(path):
            if "1203_" + SNAP_LANE in path:
                raise PermissionError("no")
            return real(path)

        with mock.patch.object(run_index, "snapshot_facts", side_effect=boom):
            rc, out = self._import()
        self.assertEqual(0, rc, out)
        self.assertEqual(1, _outcome_counts(out)[("skip", "unreadable (PermissionError)")])
        # Its duplicate is then the first copy seen, and imports.
        self.assertTrue(os.path.isdir(os.path.join(self.archive, self.lane_rid)))

    def test_a_failed_write_lets_the_second_copy_import(self):
        real = run_index.write_snapshot_folder
        calls = []

        def flaky(archive, rid, log_path, marker):
            calls.append(marker["source"])
            if len(calls) == 1:
                raise OSError("disk full")
            return real(archive, rid, log_path, marker)

        with mock.patch.object(run_index, "write_snapshot_folder", side_effect=flaky):
            rc, out = self._import("--list")
        self.assertEqual(0, rc, out)
        self.assertEqual(1, _outcome_counts(out)[("skip", "unreadable (OSError)")])
        self.assertIn("2026-10-11_1204_%s -> import imported %s" % (SNAP_LANE, self.lane_rid), out)
        self.assertTrue(os.path.isfile(os.path.join(self.archive, self.lane_rid, "snapshot.json")))


if __name__ == "__main__":
    unittest.main()
