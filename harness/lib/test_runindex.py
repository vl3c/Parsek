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
        full, _ = runindex.build_row(self.RID, _result(self.RID, "INVALID", subkind="boot", note="n", frames=0),
                                     dict(_manifest(), harnessLog=None), None)
        full_result = _result(self.RID)
        full_result["missionWallSeconds"] = 42
        self.assertEqual(("boot", "n"), (full["subkind"], full["note"]))
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
        self.a1 = _row("2026-10-11_0900_X", "INVALID", 1, "2026-10-11T09:00:00Z", lane="X")
        self.a2 = _row("2026-10-11_0900_X_a2", "PASS", 2, "2026-10-11T09:05:00Z", lane="X")
        self.b = _row("2026-10-10_0800_Y", "PASS", 1, "2026-10-10T08:00:00Z", lane="Y")

    def test_normalize_orders_and_derives_final_and_by_retry(self):
        rows = runindex.normalize([self.a2, self.a1, self.b])
        self.assertEqual(["2026-10-10_0800_Y", "2026-10-11_0900_X", "2026-10-11_0900_X_a2"],
                         [r["runId"] for r in rows])
        self.assertEqual([True, False, True], [r["final"] for r in rows])
        self.assertEqual([False, False, True], [r.get("byRetry", False) for r in rows])
        # The first attempt alone is final until its retry arrives.
        alone = runindex.normalize([self.a1])
        self.assertTrue(alone[0]["final"])

    def test_upsert_is_idempotent_byte_for_byte(self):
        rows, c1 = runindex.upsert([], [self.a1])
        self.assertEqual({"added": 1, "replaced": 0, "unchanged": 0}, c1)
        text = runindex.serialize_index(rows)
        rows2, c2 = runindex.upsert(runindex.parse_index(text)[0], [self.a1])
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
        rows, problems = runindex.parse_index(text)
        self.assertEqual(1, len(rows))
        self.assertEqual(2, len(problems))
        self.assertIn("line 2 is not JSON", problems[0])

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
            _row("2026-08-01_0900_L", "PASS", 1, "2026-08-01T09:00:00Z", 100, "L"),
            _row("2026-10-01_0900_L", "INVALID", 1, "2026-10-01T09:00:00Z", 50, "L", subkind="boot"),
            _row("2026-10-01_0900_L_a2", "PASS", 2, "2026-10-01T09:10:00Z", 120, "L"),
            _row("2026-10-20_0900_L", "PARSEK-FAIL", 1, "2026-10-20T09:00:00Z", 300, "L", subkind="ledger"),
            _row("2026-10-20_0900_L_a2", "PARSEK-FAIL", 2, "2026-10-20T09:10:00Z", 310, "L", subkind="ledger"),
            _row("2026-10-21_0900_M", "INVALID", 1, "2026-10-21T09:00:00Z", 10, "M", tier="nightly"),
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
        self.assertEqual("2026-10-20_0900_L_a2", rep["lastRun"]["runId"])
        self.assertEqual("2026-10-01_0900_L_a2", rep["lastPass"]["runId"])
        all_ = rep["windows"]["all"]
        self.assertEqual((5, 2, 2, 1), (all_["attempts"], all_["passes"], all_["fails"], all_["invalids"]))
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
        self.assertEqual({"daily": {"INVALID": 1, "PARSEK-FAIL": 2, "PASS": 2}, "nightly": {"INVALID": 1}},
                         s["byTier"])

    def test_lane_table(self):
        table = runindex.lane_table(self.rows, {"L": "daily", "N": "daily"}).splitlines()
        self.assertTrue(table[0].startswith("| Lane | Tier |"))
        self.assertEqual("| L | daily | PARSEK-FAIL(ledger) | 2026-10-01, aaaaaaaaa | 3 | 67% | 120 |", table[2])
        self.assertIn("| M | nightly | INVALID | never | 1 | 0% | 10 |", table)
        self.assertIn("| N | daily | (no run) | | 0 | | |", table)

    def test_trend_is_the_last_n_oldest_first(self):
        t = runindex.trend(self.rows, "L", 2)
        self.assertEqual(["2026-10-20_0900_L", "2026-10-20_0900_L_a2"], [x["runId"] for x in t])


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
        _write_run(self.archive, "2026-10-11_0900_L", _result("2026-10-11_0900_L", "INVALID", lane="L"),
                   log_lines=log)
        _write_run(self.archive, "2026-10-11_0900_L_a2",
                   _result("2026-10-11_0900_L_a2", "PASS", 2, "2026-10-11T09:05:00Z", lane="L"), log_lines=log)
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
        rows, problems = run_index.load_index(self.index)
        self.assertEqual([], problems)
        self.assertEqual(["2026-10-11_0900_L", "2026-10-11_0900_L_a2", "2026-10-12_0900_M",
                          "2026-10-12_1100_O"], [r["runId"] for r in rows])
        self.assertIn("2026-10-12_1000_N", out)
        self.assertIn("result json missing", out)
        self.assertIn(".2026-10-12_1200_P.archive-tmp", out)
        by_id = {r["runId"]: r for r in rows}
        self.assertEqual([1, 7, 7], by_id["2026-10-11_0900_L_a2"]["health"]["save"])
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
        counts, skipped, _ = run_index.append_runs(self.archive, ["2026-10-11_0900_L_a2", "2026-10-12_1000_N"])
        self.assertEqual(1, counts["added"])
        self.assertEqual([("2026-10-12_1000_N", "result json missing")], skipped)
        rows, _ = run_index.load_index(path)
        self.assertEqual([False, True], [r["final"] for r in rows])

    def test_append_cli_and_queries_run(self):
        self.assertEqual(0, self._main("append", "--archive", self.archive, "--run", "2026-10-11_0900_L",
                                       "--run", "2026-10-11_0900_L_a2")[0])
        rc, out = self._main("lane", "L", "--index", self.index)
        self.assertEqual(0, rc)
        self.assertIn("2026-10-11_0900_L_a2", out)
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
        return runindex.parse_index(text.decode())[0], tip

    def test_first_publish_creates_an_orphan_with_a_readme(self):
        rows = [_row("2026-10-11_0900_L", lane="L")]
        self.assertEqual("created", self._publish(self.a, rows))
        tip = run_index.remote_tip(self.a, "origin", self.BRANCH)
        parents = subprocess.run(["git", "-C", self.a, "rev-list", "--parents", "-n1", tip],
                                 capture_output=True, check=True).stdout.decode().split()
        self.assertEqual([tip], parents)
        self.assertIn(b"# Parsek harness run index", run_index.read_blob(self.a, tip, "README.md"))
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
        rows, problems = run_index.load_index(self.index)
        self.assertEqual([], problems)
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
