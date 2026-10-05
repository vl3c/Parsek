"""Unit cells for the HARNESS-OVERHEAD changes to the run.py shell and hlib.

  - the seam poll schedule (hlib.seam_poll_interval);
  - the incremental response tail (hlib.split_complete_response_lines and friends,
    and run.ResponseTail over a real file), held to PARITY with the whole-file
    reader run._read_response_lines it replaces inside the drive loop;
  - the selection-start Parsek.Tests build (hlib.classify_tests_prebuild,
    run._prebuild_tests_for_selection, the whole-selection refusal) and the -NoBuild
    plumbing of every verifier subprocess;
  - the verifier-overlap gate (hlib.verifiers_may_overlap).

The end-to-end legs (a step without a 0.25 s floor, the concurrent verifier chain)
live in test_run_smoke.HarnessOverheadSmokeTests.

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib
"""

import json
import math
import os
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
for _p in (HARNESS_ROOT, HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import run  # noqa: E402


class SeamPollIntervalTests(unittest.TestCase):

    def test_fast_inside_the_window(self):
        for elapsed in (0.0, 0.01, 1.0, hlib.SEAM_FAST_POLL_WINDOW_SECONDS - 0.001):
            with self.subTest(elapsed=elapsed):
                self.assertEqual(hlib.SEAM_FAST_POLL_SECONDS,
                                 hlib.seam_poll_interval(elapsed))

    def test_slow_from_the_window_edge_on(self):
        for elapsed in (hlib.SEAM_FAST_POLL_WINDOW_SECONDS, 2.5, 600.0):
            with self.subTest(elapsed=elapsed):
                self.assertEqual(hlib.SEAM_SLOW_POLL_SECONDS,
                                 hlib.seam_poll_interval(elapsed))

    def test_unknown_elapsed_falls_back_to_the_old_cadence_never_a_spin(self):
        for elapsed in (None, float("nan"), float("inf"), "x"):
            with self.subTest(elapsed=elapsed):
                self.assertEqual(hlib.SEAM_SLOW_POLL_SECONDS,
                                 hlib.seam_poll_interval(elapsed))

    def test_a_clock_that_stepped_back_reads_as_just_written(self):
        self.assertEqual(hlib.SEAM_FAST_POLL_SECONDS, hlib.seam_poll_interval(-0.5))

    def test_the_schedule_brackets_the_measured_frame_cadence(self):
        # The addon answers within a frame or two (~16-50 ms); the fast step must be
        # under the old 0.25 s floor and the slow step must BE the old cadence, so a
        # deferred verb parked for minutes reads no more often than before.
        self.assertLess(hlib.SEAM_FAST_POLL_SECONDS, 0.05)
        self.assertEqual(0.25, hlib.SEAM_SLOW_POLL_SECONDS)
        self.assertEqual(run.POLL_INTERVAL_SECONDS, hlib.SEAM_SLOW_POLL_SECONDS)


class SplitCompleteResponseLinesTests(unittest.TestCase):

    def test_a_torn_trailing_fragment_is_carried_not_returned(self):
        lines, carry = hlib.split_complete_response_lines(
            b"", b"id=0000 cmd=A verdict=OK\nid=0001 cmd=B verd")
        self.assertEqual(["id=0000 cmd=A verdict=OK"], lines)
        self.assertEqual(b"id=0001 cmd=B verd", carry)
        lines, carry = hlib.split_complete_response_lines(carry, b"ict=OK\n")
        self.assertEqual(["id=0001 cmd=B verdict=OK"], lines)
        self.assertEqual(b"", carry)

    def test_no_newline_yet_returns_nothing(self):
        self.assertEqual(([], b"abc"), hlib.split_complete_response_lines(b"a", b"bc"))

    def test_crlf_and_blank_lines_read_like_the_text_mode_reader(self):
        lines, carry = hlib.split_complete_response_lines(b"", b"a=1\r\n\r\n  \nb=2\n")
        self.assertEqual(["a=1", "b=2"], lines)
        self.assertEqual(b"", carry)

    def test_a_crlf_pair_split_across_reads_is_one_line_break(self):
        lines, carry = hlib.split_complete_response_lines(b"", b"a=1\r")
        self.assertEqual([], lines)
        lines, carry = hlib.split_complete_response_lines(carry, b"\nb=2\n")
        self.assertEqual(["a=1", "b=2"], lines)

    def test_torn_tail_lines_match_the_whole_file_reader(self):
        self.assertEqual([], hlib.torn_response_tail_lines(b""))
        self.assertEqual([], hlib.torn_response_tail_lines(b"   "))
        self.assertEqual(["id=0001 cmd=B"], hlib.torn_response_tail_lines(b"id=0001 cmd=B"))

    def test_restart_only_when_the_file_shrank(self):
        self.assertFalse(hlib.response_tail_restarted(0, 0))
        self.assertFalse(hlib.response_tail_restarted(10, 10))
        self.assertFalse(hlib.response_tail_restarted(10, 25))
        self.assertTrue(hlib.response_tail_restarted(10, 3))


class ResponseTailParityTests(unittest.TestCase):
    """run.ResponseTail against the whole-file reader it replaces, over a real file
    appended in ragged chunks: ``lines()`` must equal ``_read_response_lines`` after
    every append, and the terminal answer must come from complete lines only."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-tail-")
        self.path = os.path.join(self.tmp, "parsek-test-responses.txt")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _append(self, data: bytes):
        with open(self.path, "ab") as fh:
            fh.write(data)

    def test_parity_with_the_whole_file_reader_across_ragged_appends(self):
        body = ("id=0000 cmd=LoadGame verdict=OK seq=1 payload=scene%3DFLIGHT\r\n"
                "\n"
                "id=0001 cmd=SetSetting verdict=OK seq=2 msg=caf\u00e9\n"
                "id=0002 cmd=RunTests verdict=ERROR seq=3 msg=x\r\n"
                "id=0002 cmd=RunTests verdict=OK seq=9 msg=rewrite\n"
                "id=0003 cmd=FlushAndQuit verd").encode("utf-8")
        tail = run.ResponseTail(self.path)
        self.assertEqual([], tail.poll(), "a missing file reads empty")
        # Ragged chunk sizes, including a cut INSIDE the two-byte character and one
        # between a CR and its LF.
        mid_char = body.index(bytes([0xC3, 0xA9])) + 1
        mid_crlf = body.index(bytes([0x0D, 0x0A])) + 1
        cuts = sorted({0, 7, 8, 40, 41, mid_crlf, 77, 79, mid_char, 120, 160, len(body)})
        for a, b in zip(cuts, cuts[1:]):
            self._append(body[a:b])
            with self.subTest(upto=b):
                self.assertEqual(run._read_response_lines(self.path), tail.poll())
        # First-wins per id: the crash-recovery rewrite never replaces the original.
        self.assertEqual("ERROR", tail.first_terminal("0002")["verdict"])
        self.assertEqual("OK", tail.first_terminal("0000")["verdict"])
        self.assertEqual("scene%3DFLIGHT", tail.first_terminal("0000")["payload"])

    def test_a_torn_line_is_never_a_terminal_until_its_newline_lands(self):
        tail = run.ResponseTail(self.path)
        self._append(b"id=0005 cmd=RunTests verdict=O")
        tail.poll()
        self.assertIsNone(tail.first_terminal("0005"),
                          "a half-written line read 'verdict=O' as a terminal")
        self._append(b"K seq=4\n")
        tail.poll()
        self.assertEqual("OK", tail.first_terminal("0005")["verdict"])

    def test_reads_only_the_new_bytes(self):
        tail = run.ResponseTail(self.path)
        self._append(b"id=0000 cmd=A verdict=OK\n")
        tail.poll()
        consumed = tail._offset
        self._append(b"id=0001 cmd=B verdict=OK\n")
        tail.poll()
        self.assertEqual(os.path.getsize(self.path), tail._offset)
        self.assertGreater(tail._offset, consumed)

    def test_a_truncated_file_restarts_the_tail(self):
        tail = run.ResponseTail(self.path)
        self._append(b"id=0000 cmd=A verdict=ERROR\nid=0001 cmd=B verdict=OK\n")
        tail.poll()
        self.assertEqual("ERROR", tail.first_terminal("0000")["verdict"])
        with open(self.path, "wb") as fh:
            fh.write(b"id=0000 cmd=A verdict=OK\n")
        self.assertEqual(run._read_response_lines(self.path), tail.poll())
        self.assertEqual("OK", tail.first_terminal("0000")["verdict"])
        self.assertIsNone(tail.first_terminal("0001"))


class TestsPrebuildDecisionTests(unittest.TestCase):

    def test_a_clean_build_with_an_assembly_admits(self):
        d = hlib.classify_tests_prebuild(0, False, True)
        self.assertTrue(d.ok)
        self.assertEqual("", d.reason)

    def test_every_failure_leg_refuses(self):
        cases = [((0, True, True), "timed out"),
                 ((1, False, True), "failed (exit=1)"),
                 ((-1, False, False), "failed (exit=-1)"),
                 ((None, False, True), "failed (exit=None)"),
                 ((0, False, False), "left no Parsek.Tests.dll")]
        for args, needle in cases:
            with self.subTest(args=args):
                d = hlib.classify_tests_prebuild(*args)
                self.assertFalse(d.ok)
                self.assertIn(needle, d.reason)

    def test_the_build_refusal_is_terminal_and_flake_exempt(self):
        sk = hlib.TESTS_BUILD_INVALID_SUBKIND
        self.assertNotIn(sk, hlib.RETRYABLE_INVALID_SUBKINDS)
        self.assertIn(sk, hlib.FLAKE_EXEMPT_INVALID_SUBKINDS)
        self.assertFalse(hlib.should_retry(
            hlib.Verdict(hlib.VERDICT_INVALID, sk, False, "", False, ""), 1, "once"))

    def test_overlap_only_behind_a_prebuild(self):
        self.assertTrue(hlib.verifiers_may_overlap(True))
        self.assertFalse(hlib.verifiers_may_overlap(False))


class ChuteUnobservableVerdictClassTests(unittest.TestCase):
    """The mission library's chute-unobservable terminal ends a pad hop as
    MISSION-ASSERT-FAIL, the vessel-lost terminal's verdict; the harness must read it
    as a retryable driver INVALID(mission), never as PARSEK-FAIL against the mod."""

    def test_assert_fail_is_invalid_mission(self):
        met, subkind = hlib.classify_mission_step("MISSION-ASSERT-FAIL")
        self.assertFalse(met)
        self.assertEqual("mission", subkind)
        self.assertIn(subkind, hlib.RETRYABLE_INVALID_SUBKINDS)


class _ArgsRuntime(run.Runtime):
    """Captures the argv every verifier subprocess would run; runs nothing."""

    def __init__(self, build_exit=0, build_timed_out=False, assembly=True):
        self.calls = []
        self.build_exit = build_exit
        self.build_timed_out = build_timed_out
        self.assembly = assembly

    def _run(self, args, timeout, cwd, env=None):
        self.calls.append(list(args))
        if args and args[0] == "dotnet":
            return run.ToolResult(self.build_exit, self.build_timed_out,
                                  "Build FAILED.\nerror CS0001: x\n", "")
        return run.ToolResult(0, False)

    def tests_assembly_present(self):
        return self.assembly


class _MemLogger:
    def __init__(self):
        self.lines = []

    def log(self, level, step, message):
        self.lines.append((level, step, message))

    def info(self, step, msg):
        self.log("Info", step, msg)

    def warn(self, step, msg):
        self.log("Warn", step, msg)

    def verbose(self, step, msg):
        self.log("Verbose", step, msg)

    def error(self, step, msg):
        self.log("Error", step, msg)

    def text(self):
        return "\n".join(m for _, _, m in self.lines)


class NoBuildPlumbingTests(unittest.TestCase):

    def _verifier_calls(self, rt):
        rt.run_analyzer("S", fresh_gate=True, timeout=1)
        rt.run_seed_analyzer("S", "O", timeout=1)
        rt.run_log_validate("L", killed=False, no_recording=True, timeout=1)
        return rt.calls[-3:]

    def test_no_prebuild_keeps_the_scripts_building_for_themselves(self):
        rt = _ArgsRuntime()
        self.assertFalse(rt.tests_prebuilt, "the class default must be False")
        for argv in self._verifier_calls(rt):
            with self.subTest(script=argv[3]):
                self.assertNotIn("-NoBuild", argv)

    def test_a_prebuild_switches_every_verifier_to_no_build(self):
        rt = _ArgsRuntime()
        self.assertTrue(run._prebuild_tests_for_selection(rt, _MemLogger()))
        self.assertTrue(rt.tests_prebuilt)
        build = rt.calls[0]
        self.assertEqual(["dotnet", "build", run.TESTS_PROJECT_PATH], build[:3])
        self.assertIn("-p:SkipKspDeploy=true", build,
                      "the selection build must never deploy to a KSP install")
        for argv in self._verifier_calls(rt):
            with self.subTest(script=argv[3]):
                self.assertIn("-NoBuild", argv)

    def test_a_failed_build_leaves_no_build_off_and_logs_why(self):
        for kw in ({"build_exit": 1}, {"build_timed_out": True, "build_exit": -1},
                   {"assembly": False}):
            with self.subTest(kw=kw):
                rt = _ArgsRuntime(**kw)
                logger = _MemLogger()
                self.assertFalse(run._prebuild_tests_for_selection(rt, logger))
                self.assertFalse(rt.tests_prebuilt)
                self.assertIn("tests prebuild REFUSED", logger.text())
                self.assertIn("INVALID(%s)" % hlib.TESTS_BUILD_INVALID_SUBKIND,
                              logger.text())


class SelectionBuildRefusalTests(unittest.TestCase):
    """A failed selection-start build refuses EVERY selected scenario pre-boot with a
    terminal INVALID(tooling-build) row, and launches nothing."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-prebuild-")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        os.makedirs(run.RESULTS_DIR, exist_ok=True)

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_every_selected_scenario_gets_one_refusal_row(self):
        class NoLaunch(_ArgsRuntime):
            def launch(self, *a, **k):
                raise AssertionError("a refused selection must boot nothing")

        rt = NoLaunch(build_exit=1)
        specs = [{"id": "OVH-a", "tier": "daily", "instanceProfile": "stock-minimal"},
                 {"id": "OVH-b", "tier": "nightly", "instanceProfile": "stock-minimal"}]
        code = run._run_selection(specs, specs, {}, self.tmp, None, rt, _MemLogger(),
                                  None, "id=OVH-*")
        self.assertEqual(1, code)
        rows = []
        for name in sorted(os.listdir(run.RESULTS_DIR)):
            if name.endswith(".json"):
                with open(os.path.join(run.RESULTS_DIR, name), "r", encoding="utf-8") as fh:
                    rows.append(json.load(fh))
        self.assertEqual(["OVH-a", "OVH-b"], sorted(r["scenarioId"] for r in rows))
        for r in rows:
            with self.subTest(scenario=r["scenarioId"]):
                self.assertEqual(hlib.VERDICT_INVALID, r["verdict"])
                self.assertEqual(hlib.TESTS_BUILD_INVALID_SUBKIND, r["subkind"])
                self.assertEqual(0, r["wallSeconds"])
                self.assertFalse(r["admission"]["admitted"])
        self.assertEqual(1, len(rt.calls), "only the build ran")


if __name__ == "__main__":
    unittest.main()
