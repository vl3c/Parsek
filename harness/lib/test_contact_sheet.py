"""Pure tests for the V3 contact-sheet tool (`harness/tools/contact_sheet.py`)
and the hlib always-collect artifact decisions it rides on.

V3 (design-testing-unified section 6): the always-collect step copies a bounded
KSP.log + run-window screenshots into ``results/<runId>_shots/``, and the
contact-sheet tool renders per-run HTML + a newest-first index so a green run
finally leaves something a human can scan. These cells cover the pure
extraction/formatting functions against SYNTHETIC log text and a FAKE results
directory (the fake-KSP smoke conventions: no real game, no real run), plus the
binding safety contract -- the tool must be safe on every partial/missing input
(no KSP.log, no images, absent/malformed result JSON), because a contact sheet
must never crash the run flow.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import os
import shutil
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_TOOLS = os.path.join(os.path.dirname(_HERE), "tools")
for _p in (_HERE, _TOOLS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import contact_sheet as cs  # noqa: E402
import hlib  # noqa: E402


def _log_line(marker_body, subsystem="MapRenderTrace", level="VERBOSE"):
    return "[LOG 01:02:03.123] [Parsek][%s][%s] %s" % (level, subsystem, marker_body)


def _synthetic_log(probe_count_before=4, probe_count_after=4):
    """A synthetic KSP.log body: boot noise, probe frames, one anomaly bracketed
    by probe summaries, parity summaries, and a BATCH_COMPLETE tally."""
    lines = ["[LOG] boot noise", "[LOG] more noise"]
    for i in range(probe_count_before):
        lines.append(_log_line("probe frame summary frame=%d ghosts=1 sampled=1 resolveMisses=0" % (100 + i)))
    lines.append(_log_line("phase=Anomaly surface=ProtoOrbitLine pid=100037 reason=line-blink lineActive=True", level="INFO"))
    for i in range(probe_count_after):
        lines.append(_log_line("probe frame summary frame=%d ghosts=1 sampled=1 resolveMisses=0" % (200 + i)))
    lines.append(_log_line("faithful-parity summary sampled=12 overTolerance=0 skip.reaimed-or-foreign-seed=55"))
    lines.append(_log_line("faithful-parity summary sampled=1 overTolerance=0"))
    lines.append(_log_line("BATCH_COMPLETE v1 total=5 passed=5 failed=0 skipped=0 category=RecordingInvariants scene=FLIGHT",
                           subsystem="TestRunner", level="INFO"))
    return "\n".join(lines) + "\n"


class ExtractKeyLogLinesTests(unittest.TestCase):
    def test_extracts_all_three_sections(self):
        ex = cs.extract_key_log_lines(_synthetic_log())
        self.assertEqual(1, len(ex["batchComplete"]))
        self.assertIn("BATCH_COMPLETE v1 total=5", ex["batchComplete"][0])
        self.assertEqual(2, len(ex["parity"]))
        self.assertIn("faithful-parity summary sampled=12", ex["parity"][0])
        self.assertEqual(1, len(ex["anomalies"]))
        self.assertIn("reason=line-blink", ex["anomalies"][0]["line"])
        self.assertEqual({"batchComplete": 0, "parity": 0, "anomalies": 0}, ex["dropped"])

    def test_anomaly_context_is_the_nearest_n_probe_frames_each_side(self):
        """The design's 'plus/minus N surrounding frames': the NEAREST N probe
        summaries before and after the raise, in log order."""
        ex = cs.extract_key_log_lines(_synthetic_log(probe_count_before=5, probe_count_after=5))
        a = ex["anomalies"][0]
        self.assertEqual(cs.ANOMALY_CONTEXT_PROBES, len(a["before"]))
        self.assertEqual(cs.ANOMALY_CONTEXT_PROBES, len(a["after"]))
        # nearest-first ordering is log order: the LAST probes before, FIRST after.
        self.assertIn("frame=102", a["before"][0])
        self.assertIn("frame=104", a["before"][-1])
        self.assertIn("frame=200", a["after"][0])
        self.assertIn("frame=202", a["after"][-1])

    def test_anomaly_with_fewer_probes_than_n_keeps_what_exists(self):
        ex = cs.extract_key_log_lines(_synthetic_log(probe_count_before=1, probe_count_after=0))
        a = ex["anomalies"][0]
        self.assertEqual(1, len(a["before"]))
        self.assertEqual([], a["after"])

    def test_none_and_empty_and_garbage_inputs_degrade_to_empty(self):
        for text in (None, "", "no markers at all\njust noise\n"):
            ex = cs.extract_key_log_lines(text)
            self.assertEqual([], ex["batchComplete"])
            self.assertEqual([], ex["parity"])
            self.assertEqual([], ex["anomalies"])

    def test_caps_count_what_they_drop(self):
        """Silent truncation reads as 'showed everything'; the caps must say how
        much they dropped."""
        anomaly = _log_line("phase=Anomaly surface=ProtoIcon pid=1 reason=icon-teleport", level="INFO")
        text = "\n".join([anomaly] * (cs.MAX_ANOMALY_BLOCKS + 7))
        ex = cs.extract_key_log_lines(text)
        self.assertEqual(cs.MAX_ANOMALY_BLOCKS, len(ex["anomalies"]))
        self.assertEqual(7, ex["dropped"]["anomalies"])
        parity = _log_line("faithful-parity summary sampled=1 overTolerance=0")
        text = "\n".join([parity] * (cs.MAX_PARITY_LINES + 3))
        ex = cs.extract_key_log_lines(text)
        self.assertEqual(cs.MAX_PARITY_LINES, len(ex["parity"]))
        self.assertEqual(3, ex["dropped"]["parity"])

    def test_overlong_lines_are_clipped_with_a_marker_keeping_the_tail(self):
        """Head+tail clip (review NIT 8): the trailing tokens of a long line are
        often the tally (BATCH_COMPLETE totals, parity skip.* counters), so the
        clip must preserve the END of the line, not just its start."""
        line = _log_line("BATCH_COMPLETE v1 " + "x" * (cs.MAX_LINE_CHARS * 2)
                         + " total=9 passed=9 failed=0")
        ex = cs.extract_key_log_lines(line)
        shown = ex["batchComplete"][0]
        self.assertLess(len(shown), cs.MAX_LINE_CHARS + 60)
        self.assertIn("[clipped", shown)
        self.assertIn("BATCH_COMPLETE v1", shown)          # head survives
        self.assertIn("total=9 passed=9 failed=0", shown)  # tail tally survives

    def test_clip_with_zero_budget_still_clips(self):
        """Review NEW-3: line[-0:] is the WHOLE string, so a zero/one max_chars
        must not return the full line out of the one function whose job is
        bounding output."""
        for budget in (0, 1):
            shown = cs._clip("A" * 50, budget)
            self.assertNotIn("AAAAA", shown, "budget %d leaked the payload" % budget)
            self.assertIn("[clipped", shown)


class VerifierRowsTests(unittest.TestCase):
    def test_rows_from_a_well_formed_result(self):
        result = {"verifiers": {
            "analyzer": {"status": "PASS", "red": 0},
            "batchComplete": {"status": "PASS", "found": True, "failed": 0},
            "subprocessRetry": [],
        }}
        rows = cs.verifier_rows(result)
        names = [r["name"] for r in rows]
        self.assertEqual(sorted(names), names)
        analyzer = next(r for r in rows if r["name"] == "analyzer")
        self.assertEqual("PASS", analyzer["status"])
        self.assertIn("red=0", analyzer["note"])

    def test_malformed_shapes_never_raise(self):
        self.assertEqual([], cs.verifier_rows(None))
        self.assertEqual([], cs.verifier_rows("not a dict"))
        self.assertEqual([], cs.verifier_rows({"verifiers": "garbage"}))
        rows = cs.verifier_rows({"verifiers": {"weird": 42, "list": [1, 2]}})
        self.assertEqual(2, len(rows))
        self.assertEqual("-", rows[0]["status"])


class RenderHtmlTests(unittest.TestCase):
    def test_log_text_is_html_escaped(self):
        """KSP.log is hostile input (vessel names are user-authored); a raw
        <script> or <img onerror> in a log line must never reach the page
        unescaped."""
        hostile = _log_line('phase=Anomaly reason=line-blink vessel=<script>alert("x")</script>&amp;', level="INFO")
        ex = cs.extract_key_log_lines(hostile)
        page = cs.render_run_html("run-x", None, [], ex)
        self.assertNotIn("<script>alert", page)
        self.assertIn("&lt;script&gt;alert", page)

    def test_result_json_strings_are_escaped_too(self):
        result = {"runId": "r", "scenarioId": "<b>", "verdict": "PASS",
                  "note": "<i>note</i>",
                  "verifiers": {"analyzer": {"status": "PASS", "msg": "<u>"}}}
        page = cs.render_run_html("r", result, [], cs.extract_key_log_lines(""))
        self.assertNotIn("<b>", page)
        self.assertNotIn("<i>note</i>", page)
        self.assertNotIn("<u>", page)

    def test_missing_everything_still_renders_with_notes(self):
        page = cs.render_run_html("run-x", None, [], cs.extract_key_log_lines(None),
                                  log_note="no collected KSP.log for this run")
        self.assertIn("no collected KSP.log", page)
        self.assertIn("no verifier rows", page)
        self.assertIn("no screenshots captured", page)
        self.assertIn("<!doctype html>", page)

    def test_image_names_are_quoted_in_hrefs_and_escaped_in_captions(self):
        page = cs.render_run_html("run-x", None, ['shot with space & "quote".png'],
                                  cs.extract_key_log_lines(""))
        self.assertIn("shot%20with%20space%20%26%20%22quote%22.png", page)
        self.assertIn("shot with space &amp; &quot;quote&quot;.png", page)

    def test_verdict_badge_colors(self):
        for verdict, color in (("PASS", "#2e7d32"), ("PARSEK-FAIL", "#c62828"),
                               ("KILLED", "#8e0000")):
            page = cs.render_run_html("r", {"runId": "r", "verdict": verdict}, [],
                                      cs.extract_key_log_lines(""))
            self.assertIn(color, page)
        page = cs.render_run_html("r", {"runId": "r", "verdict": "SOMETHING-NEW"}, [],
                                  cs.extract_key_log_lines(""))
        self.assertIn(cs.DEFAULT_VERDICT_COLOR, page)


class IndexTests(unittest.TestCase):
    def test_entries_sort_newest_first_with_runid_tiebreak(self):
        entries = [
            {"runId": "2026-07-01_0900_A", "utc": "2026-07-01T09:30:00Z"},
            {"runId": "2026-07-02_0900_B", "utc": "2026-07-02T09:30:00Z"},
            {"runId": "2026-07-02_0900_A", "utc": "2026-07-02T09:30:00Z"},
            {"runId": "zzz-no-utc", "utc": ""},
        ]
        ordered = cs.sort_entries_newest_first(entries)
        self.assertEqual(["2026-07-02_0900_B", "2026-07-02_0900_A",
                          "2026-07-01_0900_A", "zzz-no-utc"],
                         [e["runId"] for e in ordered])

    def test_run_index_entry_tolerates_garbage(self):
        e = cs.run_index_entry(None, "fallback-run")
        self.assertEqual("fallback-run", e["runId"])
        self.assertEqual("UNREADABLE", e["verdict"])
        e = cs.run_index_entry({"runId": "r1", "verdict": "PASS", "wallSeconds": "nan-string"},
                               "fallback")
        self.assertIsNone(e["wallSeconds"])


class FakeResultsDirTests(unittest.TestCase):
    """The I/O shell over a FAKE results directory (smoke-harness conventions:
    real files, no real game)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-contact-sheet-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _write(self, name, text):
        path = os.path.join(self.tmp, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)
        return path

    def _result_json(self, run_id, verdict="PASS", ended="2026-07-31T10:00:00Z"):
        return ('{"schema": 1, "runId": "%s", "scenarioId": "S-X", "verdict": "%s", '
                '"subkind": "", "endedUtc": "%s", "wallSeconds": 42, "note": "", '
                '"verifiers": {"analyzer": {"status": "PASS", "red": 0}}}'
                % (run_id, verdict, ended))

    def test_full_run_sheet_from_fake_dir(self):
        run_id = "2026-07-31_1000_S-X"
        self._write("%s.json" % run_id, self._result_json(run_id))
        self._write("%s_shots/KSP.log" % run_id, _synthetic_log())
        shots = os.path.join(self.tmp, run_id + "_shots")
        with open(os.path.join(shots, "frame001.png"), "wb") as fh:
            fh.write(b"\x89PNG fake")
        self._write("%s_shots/notes.txt" % run_id, "not an image")

        sheet = cs.generate_run_sheet(self.tmp, run_id)
        self.assertTrue(os.path.isfile(sheet))
        with open(sheet, "r", encoding="utf-8") as fh:
            page = fh.read()
        self.assertIn("BATCH_COMPLETE v1 total=5", page)
        self.assertIn("faithful-parity summary", page)
        self.assertIn("reason=line-blink", page)
        self.assertIn("frame001.png", page)
        self.assertNotIn("notes.txt", page)          # non-image never enters the grid
        self.assertIn("analyzer", page)

    def test_sheet_survives_every_missing_input(self):
        # no result JSON, no shots dir at all
        sheet = cs.generate_run_sheet(self.tmp, "ghost-run")
        self.assertTrue(os.path.isfile(sheet))
        # malformed result JSON, empty log
        self._write("bad-run.json", "{not json")
        self._write("bad-run_shots/KSP.log", "")
        sheet = cs.generate_run_sheet(self.tmp, "bad-run")
        with open(sheet, "r", encoding="utf-8") as fh:
            page = fh.read()
        self.assertIn("no verifier rows", page)

    def test_index_lists_newest_first_and_skips_non_run_json(self):
        self._write("2026-07-30_0900_A.json",
                    self._result_json("2026-07-30_0900_A", "PASS", "2026-07-30T09:10:00Z"))
        self._write("2026-07-31_0900_B.json",
                    self._result_json("2026-07-31_0900_B", "PARSEK-FAIL", "2026-07-31T09:10:00Z"))
        self._write("2026-07-31_0900_B_mission.json", '{"schema": 1, "verdict": "MISSION-OK"}')
        self._write("2026-07-31_0900_B.manifest.json", '{"schema": 1}')
        self._write("broken.json", "{definitely not json")
        # a sheet exists only for run A -> only A's row links
        cs.generate_run_sheet(self.tmp, "2026-07-30_0900_A")

        index = cs.generate_index(self.tmp)
        with open(index, "r", encoding="utf-8") as fh:
            page = fh.read()
        self.assertNotIn("_mission", page)
        self.assertNotIn("manifest", page)
        self.assertIn("UNREADABLE", page)             # broken.json surfaces, gray
        pos_b = page.index("2026-07-31_0900_B")
        pos_a = page.index("2026-07-30_0900_A")
        self.assertLess(pos_b, pos_a, "index must list runs newest-first")
        self.assertIn('href="2026-07-30_0900_A_contact.html"', page)
        self.assertIn("#c62828", page)                # PARSEK-FAIL red badge
        self.assertIn("#2e7d32", page)                # PASS green badge

    def test_cli_main_generates_everything_and_exits_zero(self):
        run_id = "2026-07-31_1000_S-X"
        self._write("%s.json" % run_id, self._result_json(run_id))
        rc = cs.main(["--results-dir", self.tmp])
        self.assertEqual(0, rc)
        self.assertTrue(os.path.isfile(os.path.join(self.tmp, run_id + "_contact.html")))
        self.assertTrue(os.path.isfile(os.path.join(self.tmp, "index.html")))

    def test_index_only_flag(self):
        self._write("r1.json", self._result_json("r1"))
        rc = cs.main(["--results-dir", self.tmp, "--index-only"])
        self.assertEqual(0, rc)
        self.assertTrue(os.path.isfile(os.path.join(self.tmp, "index.html")))
        self.assertFalse(os.path.isfile(os.path.join(self.tmp, "r1_contact.html")))


class ArtifactLogCopyPlanTests(unittest.TestCase):
    """hlib.plan_artifact_log_copy: the bounded-KSP.log-copy decision."""

    def test_small_and_exact_cap_copy_all(self):
        self.assertTrue(hlib.plan_artifact_log_copy(0).copy_all)
        self.assertTrue(hlib.plan_artifact_log_copy(1024).copy_all)
        self.assertTrue(hlib.plan_artifact_log_copy(hlib.ARTIFACT_LOG_CAP_BYTES).copy_all)

    def test_oversize_keeps_head_plus_tail_within_cap(self):
        plan = hlib.plan_artifact_log_copy(hlib.ARTIFACT_LOG_CAP_BYTES + 1)
        self.assertFalse(plan.copy_all)
        self.assertEqual(hlib.ARTIFACT_LOG_HEAD_BYTES, plan.head_bytes)
        self.assertEqual(hlib.ARTIFACT_LOG_TAIL_BYTES, plan.tail_bytes)
        self.assertLessEqual(plan.head_bytes + plan.tail_bytes, hlib.ARTIFACT_LOG_CAP_BYTES)

    def test_inconsistent_caps_clamp_and_tail_wins(self):
        """head+tail must never exceed the cap; the tail (BATCH_COMPLETE +
        teardown) wins the clamp."""
        plan = hlib.plan_artifact_log_copy(1000, cap_bytes=100, head_bytes=80, tail_bytes=80)
        self.assertFalse(plan.copy_all)
        self.assertEqual(80, plan.tail_bytes)
        self.assertEqual(20, plan.head_bytes)
        plan = hlib.plan_artifact_log_copy(1000, cap_bytes=50, head_bytes=80, tail_bytes=80)
        self.assertEqual(50, plan.tail_bytes)
        self.assertEqual(0, plan.head_bytes)

    def test_negative_size_reads_as_copy_all(self):
        self.assertTrue(hlib.plan_artifact_log_copy(-1).copy_all)


class SelectRunScreenshotsTests(unittest.TestCase):
    """hlib.select_run_screenshots: which Screenshots/ files belong to the run."""

    START = 1000.0

    def test_prior_run_files_are_filtered_by_mtime(self):
        candidates = [("old.png", 100.0, 10), ("new.png", 1500.0, 10)]
        selected, prior, over = hlib.select_run_screenshots(candidates, self.START)
        self.assertEqual(["new.png"], selected)
        self.assertEqual(1, prior)
        self.assertEqual(0, over)

    def test_mtime_slack_admits_a_boundary_file(self):
        candidates = [("edge.png", self.START - 1.0, 10)]
        selected, prior, _ = hlib.select_run_screenshots(candidates, self.START)
        self.assertEqual(["edge.png"], selected)
        self.assertEqual(0, prior)

    def test_non_image_files_are_ignored_entirely(self):
        candidates = [("readme.txt", 2000.0, 10), ("shot.PNG", 2000.0, 10),
                      ("noext", 2000.0, 10)]
        selected, prior, over = hlib.select_run_screenshots(candidates, self.START)
        self.assertEqual(["shot.PNG"], selected)   # case-insensitive extension
        self.assertEqual(0, prior)
        self.assertEqual(0, over)

    def test_count_cap_keeps_chronological_order_and_counts_the_rest(self):
        candidates = [("s%03d.png" % i, 2000.0 + i, 1) for i in range(10)]
        selected, _, over = hlib.select_run_screenshots(candidates, self.START, max_count=4)
        self.assertEqual(["s000.png", "s001.png", "s002.png", "s003.png"], selected)
        self.assertEqual(6, over)

    def test_byte_cap_trips_and_counts_the_rest(self):
        candidates = [("a.png", 2000.0, 60), ("b.png", 2001.0, 60), ("c.png", 2002.0, 60)]
        selected, _, over = hlib.select_run_screenshots(candidates, self.START, max_bytes=100)
        self.assertEqual(["a.png"], selected)
        self.assertEqual(2, over)

    def test_byte_cap_is_greedy_fill_not_stop_on_trip(self):
        """Review NIT 7, now the DOCUMENTED contract: an oversize capture is
        skipped but later, smaller captures still ride -- one huge file must
        not evict every capture after it."""
        candidates = [("a.png", 2000.0, 10), ("big.png", 2001.0, 999), ("c.png", 2002.0, 10)]
        selected, _, over = hlib.select_run_screenshots(candidates, self.START, max_bytes=100)
        self.assertEqual(["a.png", "c.png"], selected)
        self.assertEqual(1, over)

    def test_caps_resolve_module_constants_at_call_time(self):
        """Review NIT 6: def-time defaults would freeze the constants, making a
        rebound cap inert -- and the truncation path untestable without a real
        64 MiB file."""
        orig = hlib.ARTIFACT_LOG_CAP_BYTES
        try:
            hlib.ARTIFACT_LOG_CAP_BYTES = 100
            self.assertFalse(hlib.plan_artifact_log_copy(1000).copy_all)
        finally:
            hlib.ARTIFACT_LOG_CAP_BYTES = orig
        self.assertTrue(hlib.plan_artifact_log_copy(1000).copy_all)


class SelectShotsDirsToPruneTests(unittest.TestCase):
    """hlib.select_shots_dirs_to_prune (review MAJOR 1): the retention decision
    bounding results/*_shots growth across runs."""

    def test_under_both_budgets_prunes_nothing(self):
        entries = [("a_shots", 1.0, 10), ("b_shots", 2.0, 10)]
        self.assertEqual([], hlib.select_shots_dirs_to_prune(
            entries, keep_dirs=5, max_total_bytes=1000))

    def test_count_budget_prunes_oldest_first(self):
        entries = [("old_shots", 1.0, 10), ("mid_shots", 2.0, 10), ("new_shots", 3.0, 10)]
        prune = hlib.select_shots_dirs_to_prune(entries, keep_dirs=2, max_total_bytes=10**9)
        self.assertEqual(["old_shots"], prune)

    def test_byte_budget_prunes_past_the_cap(self):
        entries = [("old_shots", 1.0, 60), ("mid_shots", 2.0, 60), ("new_shots", 3.0, 60)]
        prune = hlib.select_shots_dirs_to_prune(entries, keep_dirs=10, max_total_bytes=130)
        self.assertEqual(["old_shots"], prune)

    def test_protected_dir_is_always_kept_even_over_budget(self):
        """The run that just collected must never prune itself, even when its
        own dir alone busts the byte budget."""
        entries = [("huge_shots", 5.0, 10**9), ("old_shots", 1.0, 10)]
        prune = hlib.select_shots_dirs_to_prune(entries, protect_name="huge_shots",
                                                keep_dirs=1, max_total_bytes=100)
        self.assertNotIn("huge_shots", prune)
        self.assertEqual(["old_shots"], prune)

    def test_prune_list_is_oldest_first(self):
        entries = [("a_shots", 1.0, 10), ("b_shots", 2.0, 10),
                   ("c_shots", 3.0, 10), ("d_shots", 4.0, 10)]
        prune = hlib.select_shots_dirs_to_prune(entries, keep_dirs=1, max_total_bytes=10**9)
        self.assertEqual(["a_shots", "b_shots", "c_shots"], prune)

    def test_byte_budget_is_stop_on_trip_newest_wins(self):
        """Review NEW-4 (CONFIRMED against the first fix): retention priority is
        strictly newest-wins -- once the byte budget trips, everything OLDER is
        pruned too. A large recent run must never be evicted while tiny older
        runs survive past it."""
        entries = [("newest_shots", 100.0, 500), ("older_shots", 50.0, 10),
                   ("oldest_shots", 1.0, 10)]
        prune = hlib.select_shots_dirs_to_prune(entries, keep_dirs=10, max_total_bytes=100)
        # newest busts the budget -> it AND everything older goes; nothing
        # older can outlive a newer dir.
        self.assertEqual(["oldest_shots", "older_shots", "newest_shots"], prune)
        # ... and with room for the newest, the trip point prunes only older.
        entries = [("newest_shots", 100.0, 90), ("older_shots", 50.0, 20),
                   ("oldest_shots", 1.0, 5)]
        prune = hlib.select_shots_dirs_to_prune(entries, keep_dirs=10, max_total_bytes=100)
        self.assertEqual(["oldest_shots", "older_shots"], prune)


class NonFiniteWallSecondsTests(unittest.TestCase):
    """Review MINOR 2 (CONFIRMED by the reviewer's repro): json.load admits bare
    Infinity/NaN, and int(inf) raises -- one poison result JSON must not
    permanently break every subsequent index render."""

    def test_run_index_entry_drops_non_finite_and_bool_wall(self):
        for bad in (float("inf"), float("-inf"), float("nan"), True):
            e = cs.run_index_entry({"runId": "r", "verdict": "PASS", "wallSeconds": bad}, "r")
            self.assertIsNone(e["wallSeconds"], "wallSeconds=%r must read as None" % bad)

    def test_big_int_wall_does_not_raise_through_the_guard(self):
        """Review NEW-1 (CONFIRMED regression in the first fix): math.isfinite
        coerces to a C double, so isfinite(10**400) raises OverflowError INSIDE
        the guard. A Python int is always finite -- type check only."""
        big = 10 ** 400
        e = cs.run_index_entry({"runId": "r", "verdict": "PASS", "wallSeconds": big}, "r")
        self.assertEqual(big, e["wallSeconds"])
        # ... and the full pages render, not raise.
        page = cs.render_run_html("r", {"runId": "r", "verdict": "PASS",
                                        "wallSeconds": big}, [],
                                  cs.extract_key_log_lines(""))
        self.assertIn("<!doctype html>", page)
        self.assertIn("<!doctype html>", cs.render_index_html(
            [cs.run_index_entry({"runId": "r", "verdict": "PASS", "wallSeconds": big}, "r")]))

    def test_poison_result_json_still_renders_sheet_and_index(self):
        tmp = tempfile.mkdtemp(prefix="parsek-contact-poison-")
        try:
            with open(os.path.join(tmp, "poison.json"), "w", encoding="utf-8") as fh:
                fh.write('{"schema": 1, "runId": "poison", "verdict": "PASS", '
                         '"wallSeconds": Infinity}')
            sheet = cs.generate_run_sheet(tmp, "poison")
            self.assertTrue(os.path.isfile(sheet))
            index = cs.generate_index(tmp)
            self.assertTrue(os.path.isfile(index))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
