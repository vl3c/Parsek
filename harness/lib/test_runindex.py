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
        self.assertEqual("| L | daily | PARSEK-FAIL(ledger) | 2026-10-01, aaaaaaaaa | 3 | 67% | 120 |", table[2])
        self.assertIn("| M | nightly | INVALID | never | 1 | 0% | 10 |", table)
        self.assertIn("| N | daily | (no run) | | 0 | | |", table)

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
        self.assertIn(b"# Parsek harness run index", run_index.read_blob(self.a, tip, "RUN-INDEX.md"))
        self.assertIsNone(run_index.read_blob(self.a, tip, "README.md"))
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
                        "data/deep/notes.txt": b"nested\n"}
        for name, data in self.foreign.items():
            path = os.path.join(seed, *name.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as fh:
                fh.write(data)
        ident = ["-c", "user.name=t", "-c", "user.email=t@localhost", "-c", "core.autocrlf=false"]
        subprocess.run(["git", "-C", seed] + ident + ["add", "-A"], check=True)
        subprocess.run(["git", "-C", seed] + ident + ["commit", "--quiet", "-m", "seed"], check=True)
        subprocess.run(["git", "-C", seed, "push", "--quiet", self.bare, "HEAD:refs/heads/%s" % self.BRANCH],
                       check=True, capture_output=True)

    def _assert_foreign_intact(self, repo):
        tip = run_index.remote_tip(repo, "origin", self.BRANCH)
        for name, data in self.foreign.items():
            self.assertEqual(data, run_index.read_blob(repo, tip, name), name)
        listing = subprocess.run(["git", "-C", repo, "ls-tree", "-r", "--name-only", tip],
                                 capture_output=True, check=True).stdout.decode().split()
        self.assertEqual(sorted(set(self.foreign) | set(run_index.OWN_FILES) - {"lane-health.json"}),
                         sorted(listing))
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
        with self.assertRaises(RuntimeError):
            run_index._commit_files(self.a, None, {"README.md": b"x"}, "m")

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


if __name__ == "__main__":
    unittest.main()
