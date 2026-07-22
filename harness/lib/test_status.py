"""Unit tests for the pure parsing/analysis helpers in harness/status.py
(the live-observability status CLI, design-live-observability.md Phase 1).

status.py lives at the harness root (it is a root-level CLI, not a library),
so this test module bootstraps the parent directory onto sys.path -- the same
pattern mission_runner.py uses for missions/lib. Runs under the standard
discovery root: ``python -m unittest discover -s lib -q``.

ASCII only; stdlib only.
"""

import json
import math
import os
import sys
import tempfile
import time
import unittest

_HARNESS_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _HARNESS_DIR not in sys.path:
    sys.path.insert(0, _HARNESS_DIR)

import status  # noqa: E402


# A REAL telemetry line from the 2026-07-22_1210 B5 flight (PLAN-CORRECTION
# 1x block) -- the parser contract is pinned to the live format.
REAL_TELEMETRY = (
    "[Mission][VerboseRateLimited][PLAN-CORRECTION] telemetry "
    "ap=19636235.647 pe=75440.669 ecc=0.935 inc=0.814 alt=6207553.448 "
    "vspd=774.599 body=Kerbin nodes=0 nodeDv=nan nodeUt=nan tts=7361.533 "
    "warpTo=nan lf=742.682 thr=0.000 situation=ORBITING warp=NONEx1.000 "
    "apErr=nan")

REAL_TRANSITION = (
    "[Mission][Info][COAST-TO-TARGET] phase PLAN-CORRECTION -> "
    "COAST-TO-TARGET ut=7739.041 alt=6280389.831 ap=19636235.646 "
    "vsurf=768.862")

REAL_OVERCAP_WARN = (
    "[Mission][Warn][Plan] course-correction dv 172.9 m/s exceeds cap "
    "150.0; plan removed (correction disqualified, coast will fly the raw "
    "intercept)")


def _telem(phase="PLAN-CORRECTION", tts=1000.0, node_dv="nan", nodes=0,
           warp="NONEx1.000", thr=0.0, warp_to="nan"):
    return ("[Mission][VerboseRateLimited][%s] telemetry ap=1.0 pe=1.0 "
            "ecc=0.5 inc=0.1 alt=100.0 vspd=1.0 body=Kerbin nodes=%d "
            "nodeDv=%s nodeUt=nan tts=%s warpTo=%s lf=10.0 thr=%.3f "
            "situation=ORBITING warp=%s apErr=nan"
            % (phase, nodes, node_dv, tts, warp_to, thr, warp))


class ParseLogLineTests(unittest.TestCase):
    def test_mission_line(self):
        parsed = status.parse_log_line(REAL_OVERCAP_WARN)
        self.assertEqual(parsed["source"], "Mission")
        self.assertEqual(parsed["level"], "Warn")
        self.assertEqual(parsed["tag"], "Plan")
        self.assertTrue(parsed["message"].startswith("course-correction"))

    def test_harness_line(self):
        parsed = status.parse_log_line(
            "[Harness][Info][Drive] drive resp id=0001 verdict=OK met=True")
        self.assertEqual(parsed["source"], "Harness")
        self.assertEqual(parsed["tag"], "Drive")

    def test_non_log_lines_are_none(self):
        self.assertIsNone(status.parse_log_line(""))
        self.assertIsNone(status.parse_log_line("Traceback (most recent"))
        self.assertIsNone(status.parse_log_line("[Other][Info][X] nope"))


class ParseTelemetryTests(unittest.TestCase):
    def test_real_line_decodes(self):
        parsed = status.parse_log_line(REAL_TELEMETRY)
        telem = status.parse_telemetry_message(parsed["message"])
        self.assertAlmostEqual(telem["ap"], 19636235.647)
        self.assertAlmostEqual(telem["pe"], 75440.669)
        self.assertAlmostEqual(telem["tts"], 7361.533)
        self.assertEqual(telem["body"], "Kerbin")
        self.assertEqual(telem["situation"], "ORBITING")
        self.assertEqual(telem["nodes"], 0)
        self.assertTrue(math.isnan(telem["nodeDv"]))
        self.assertTrue(math.isnan(telem["warpTo"]))
        self.assertEqual(telem["warp_mode"], "NONE")
        self.assertAlmostEqual(telem["warp_rate"], 1.0)

    def test_rails_warp_split(self):
        telem = status.parse_telemetry_message(
            "telemetry ap=1 warp=RAILSx1000.000")
        self.assertEqual(telem["warp_mode"], "RAILS")
        self.assertAlmostEqual(telem["warp_rate"], 1000.0)

    def test_non_telemetry_is_none(self):
        self.assertIsNone(status.parse_telemetry_message("phase A -> B ut=1"))


class ParsePhaseTransitionTests(unittest.TestCase):
    def test_real_transition(self):
        parsed = status.parse_log_line(REAL_TRANSITION)
        trans = status.parse_phase_transition(parsed["message"])
        self.assertEqual(trans["from"], "PLAN-CORRECTION")
        self.assertEqual(trans["to"], "COAST-TO-TARGET")
        self.assertAlmostEqual(trans["ut"], 7739.041)
        self.assertAlmostEqual(trans["alt"], 6280389.831)

    def test_non_transition_is_none(self):
        self.assertIsNone(status.parse_phase_transition("telemetry ap=1"))


class ParseActionTests(unittest.TestCase):
    def test_action_with_text(self):
        act = status.parse_action_message(
            "action set_target_body value=none text=Mun")
        self.assertEqual(act["kind"], "set_target_body")
        self.assertEqual(act["text"], "Mun")

    def test_action_plain(self):
        act = status.parse_action_message(
            "action warp_to_ut value=14946.501")
        self.assertEqual(act["kind"], "warp_to_ut")
        self.assertEqual(act["value"], "14946.501")


class RunIdTests(unittest.TestCase):
    def test_first_attempt(self):
        parts = status.split_run_id("2026-07-22_1210_B5-mun-flyby")
        self.assertEqual(parts["ts"], "2026-07-22_1210")
        self.assertEqual(parts["scenario"], "B5-mun-flyby")
        self.assertEqual(parts["attempt"], 1)

    def test_retry_attempt_suffix(self):
        parts = status.split_run_id("2026-07-21_2338_B5-mun-flyby_a2")
        self.assertEqual(parts["scenario"], "B5-mun-flyby")
        self.assertEqual(parts["attempt"], 2)

    def test_start_epoch(self):
        epoch = status.run_start_epoch("2026-07-22_1210_B5-mun-flyby")
        self.assertIsNotNone(epoch)

    def test_unparseable_falls_back(self):
        parts = status.split_run_id("weird-name")
        self.assertEqual(parts["scenario"], "weird-name")
        self.assertEqual(parts["attempt"], 1)


class SummaryAndPhaseRowsTests(unittest.TestCase):
    def _lines(self):
        return [
            "[Mission][Info][Spawn] mission start name=b5_mun_flyby "
            "rpc=127.0.0.1:50000 stream=50001 budget=2400.000s result=r.json",
            "[Mission][Info][MJ-ASCENT] phase PRELAUNCH -> MJ-ASCENT "
            "ut=100.0 alt=7.8 ap=80.2 vsurf=0.0",
            _telem("MJ-ASCENT", tts="nan"),
            _telem("MJ-ASCENT", tts="nan"),
            "[Mission][Info][CIRCULARIZE] phase MJ-ASCENT -> CIRCULARIZE "
            "ut=350.0 alt=70000 ap=80000 vsurf=2000",
            _telem("CIRCULARIZE", tts="nan"),
        ]

    def test_summary_counts(self):
        summary = status.summarize_mission_lines(self._lines())
        self.assertEqual(len(summary["transitions"]), 2)
        self.assertEqual(len(summary["telemetry"]), 3)
        self.assertEqual(summary["spawn"].get("name"), "b5_mun_flyby")
        self.assertIsNone(summary["verdict"])

    def test_phase_rows_durations(self):
        summary = status.summarize_mission_lines(self._lines())
        rows = status.build_phase_rows(summary)
        phases = [r["phase"] for r in rows]
        self.assertEqual(phases, ["PRELAUNCH", "MJ-ASCENT", "CIRCULARIZE"])
        ascent = rows[1]
        self.assertAlmostEqual(ascent["game_s"], 250.0)
        self.assertAlmostEqual(ascent["wall_est_s"], 2.0)
        self.assertIsNone(rows[2]["game_s"])  # open current phase

    def test_verdict_capture(self):
        lines = self._lines() + [
            "[Mission][Info][Verdict] mission verdict=MISSION-FLAKE "
            "reason=phase COAST-TO-TARGET timed out phasesReached=[] "
            "wall=663.567s"]
        summary = status.summarize_mission_lines(lines)
        self.assertEqual(summary["verdict"]["verdict"], "MISSION-FLAKE")


class ElapsedGameEstimateTests(unittest.TestCase):
    def test_tts_drift_estimates_game_seconds(self):
        lines = [
            "[Mission][Info][PLAN-CORRECTION] phase COAST-TO-TARGET -> "
            "PLAN-CORRECTION ut=7000.0 alt=1 ap=1 vsurf=1",
            _telem(tts=7400.0),
            _telem(tts=7350.0),
            _telem(tts=7300.0),
        ]
        summary = status.summarize_mission_lines(lines)
        self.assertAlmostEqual(
            status.estimate_phase_elapsed_game(summary), 100.0)

    def test_non_finite_tts_gives_none(self):
        lines = [
            "[Mission][Info][X] phase A -> X ut=1.0 alt=1 ap=1 vsurf=1",
            _telem(tts="nan"), _telem(tts="nan")]
        summary = status.summarize_mission_lines(lines)
        self.assertIsNone(status.estimate_phase_elapsed_game(summary))

    def test_phase2_ut_token_is_exact_and_preferred(self):
        """Phase-2 telemetry lines end in ut=; the estimator uses entry-ut ->
        last-ut directly and ignores the (here contradictory) tts drift."""
        lines = [
            "[Mission][Info][X] phase A -> X ut=1000.0 alt=1 ap=1 vsurf=1",
            _telem(tts=500.0) + " ut=1010.0",
            _telem(tts=499.0) + " ut=1042.0",
        ]
        summary = status.summarize_mission_lines(lines)
        self.assertAlmostEqual(
            status.estimate_phase_elapsed_game(summary), 42.0)


class BudgetMappingTests(unittest.TestCase):
    PARAMS = {"planTimeoutSeconds": 300, "coastTimeoutSeconds": 400000,
              "ascentTimeoutSeconds": 420,
              "transferBurnTimeoutSeconds": 4000}

    def test_plan_phases_share_plan_timeout(self):
        self.assertEqual(
            status.phase_budget_seconds("PLAN-CORRECTION", self.PARAMS), 300)
        self.assertEqual(
            status.phase_budget_seconds("PLAN-TRANSFER", self.PARAMS), 300)

    def test_untimed_phase_is_none(self):
        self.assertIsNone(status.phase_budget_seconds("RETURN", self.PARAMS))
        self.assertIsNone(status.phase_budget_seconds("ORBIT", self.PARAMS))


class HeuristicTests(unittest.TestCase):
    """The heuristic must NAME the over-cap plan-removal loop that looked
    like a silent 1x hang on the 2026-07-22 B5 flights."""

    PARAMS = {"planTimeoutSeconds": 300, "planRetrySeconds": 30,
              "maxCorrectionDvMps": 150}

    def _overcap_block(self):
        lines = [
            "[Mission][Info][PLAN-CORRECTION] phase COAST-TO-TARGET -> "
            "PLAN-CORRECTION ut=7400.0 alt=1 ap=1 vsurf=1",
            _telem(tts=7400.0),
            REAL_OVERCAP_WARN,
            "[Mission][Info][PLAN-CORRECTION] action mj_plan_course_correct "
            "value=60000.000",
            _telem(tts=7370.0),
            REAL_OVERCAP_WARN,
            "[Mission][Info][PLAN-CORRECTION] action mj_plan_course_correct "
            "value=60000.000",
            _telem(tts=7340.0),
        ]
        return lines

    def test_overcap_loop_is_named(self):
        summary = status.summarize_mission_lines(self._overcap_block())
        line = status.derive_heuristic(summary, self.PARAMS)
        self.assertIn("OVER-CAP", line)
        self.assertIn("2 plan(s) removed", line)
        self.assertIn("172.9", line)
        self.assertIn("150.0", line)
        self.assertIn("silent 1x hang", line)
        # tts drifted 60 game-s of a 300 s budget: fall-through in ~4m00s.
        self.assertIn("4m00s", line)

    def test_finished_run_reports_verdict(self):
        lines = self._overcap_block() + [
            "[Mission][Info][Verdict] mission verdict=MISSION-OK reason=ok "
            "phasesReached=[] wall=625.0s"]
        summary = status.summarize_mission_lines(lines)
        line = status.derive_heuristic(summary, self.PARAMS)
        self.assertIn("RUN FINISHED", line)
        self.assertIn("MISSION-OK", line)

    def test_burn_phase_static_node_dv(self):
        lines = [
            "[Mission][Info][CORRECTION-BURN] phase PLAN-CORRECTION -> "
            "CORRECTION-BURN ut=8000.0 alt=1 ap=1 vsurf=1",
        ]
        for _ in range(30):
            lines.append(_telem("CORRECTION-BURN", tts="nan",
                                node_dv="42.500", nodes=1))
        summary = status.summarize_mission_lines(lines)
        line = status.derive_heuristic(summary, {})
        self.assertIn("UNCHANGED", line)
        self.assertIn("42.5", line)
        self.assertIn("watchdog", line)

    def test_coast_native_warp_named(self):
        lines = [
            "[Mission][Info][COAST-TO-TARGET] phase PLAN-CORRECTION -> "
            "COAST-TO-TARGET ut=7739.0 alt=1 ap=1 vsurf=1",
            _telem("COAST-TO-TARGET", tts=5950.7, warp="RAILSx1000.000",
                   warp_to="14946.501"),
        ]
        summary = status.summarize_mission_lines(lines)
        line = status.derive_heuristic(summary, {})
        self.assertIn("native warp_to active", line)
        self.assertIn("14946.501", line)

    def test_coast_stuck_at_1x_flagged(self):
        lines = [
            "[Mission][Info][COAST-TO-TARGET] phase X -> COAST-TO-TARGET "
            "ut=7739.0 alt=1 ap=1 vsurf=1",
            _telem("COAST-TO-TARGET", tts=5950.7, warp="NONEx1.000",
                   warp_to="nan"),
        ]
        summary = status.summarize_mission_lines(lines)
        line = status.derive_heuristic(summary, {})
        self.assertIn("NO warp commanded", line)
        self.assertIn("[Warp]", line)


class EventFilterTests(unittest.TestCase):
    def test_telemetry_is_not_an_event(self):
        parsed = status.parse_log_line(REAL_TELEMETRY)
        self.assertFalse(status.is_event_line(parsed))

    def test_plan_warn_is_an_event(self):
        parsed = status.parse_log_line(REAL_OVERCAP_WARN)
        self.assertTrue(status.is_event_line(parsed))

    def test_action_is_an_event(self):
        parsed = status.parse_log_line(
            "[Mission][Info][COAST-TO-TARGET] action warp_to_ut "
            "value=14946.501")
        self.assertTrue(status.is_event_line(parsed))


class FormatterTests(unittest.TestCase):
    def test_fmt_meters(self):
        self.assertEqual(status.fmt_meters(6207553.448), "6207.6 km")
        self.assertEqual(status.fmt_meters(80.2), "80.2 m")
        self.assertEqual(status.fmt_meters(float("nan")), "n/a")

    def test_fmt_duration(self):
        self.assertEqual(status.fmt_duration(42), "42s")
        self.assertEqual(status.fmt_duration(270), "4m30s")
        self.assertEqual(status.fmt_duration(7385), "2h03m")
        self.assertEqual(status.fmt_duration(None), "n/a")

    def test_decode_telemetry_one_field_per_line(self):
        telem = status.parse_telemetry_message(
            status.parse_log_line(REAL_TELEMETRY)["message"])
        rendered = status.decode_telemetry_fields(telem)
        self.assertEqual(len(rendered), len(status.TELEMETRY_FIELD_LABELS))
        self.assertTrue(any("6207.6 km" in r for r in rendered))
        self.assertTrue(any("NONE x1" in r for r in rendered))


class StatusFilePreferredPathTests(unittest.TestCase):
    """Phase 2 contract check (design-live-observability 2d): a fresh
    results/<runId>_status.json written by the mission's StatusFileWriter is
    picked up by the panel with NO status.py changes -- the machine block
    renders verbatim; a stale one falls back to log parsing."""

    RUN_ID = "2026-07-22_1400_B5-mun-flyby"

    def _payload(self):
        return {"schema": 1, "mission": "b5_mun_flyby", "rpcPort": 50000,
                "phase": "PLAN-CORRECTION",
                "phasesReached": ["PRELAUNCH", "PLAN-CORRECTION"],
                "machine": {"phase": "PLAN-CORRECTION", "rounds": 1,
                            "planAttempts": 3, "bodyBlank": 0,
                            "corrBurnStarted": False, "alignedStreak": 0,
                            "minNodeDv": None, "warpCmd": 0,
                            "warpToCmd": None, "plannedNodes": 0,
                            "burnStaticAge": None},
                "snapshot": {"ut": 7400.0, "body": "Kerbin"},
                "events": ["[Mission][Warn][Plan] course-correction dv "
                           "172.9 m/s exceeds cap 150.0; plan removed"],
                "wallWritten": time.time()}

    def _write_run(self, tmp, stale=False):
        log_path = os.path.join(tmp, self.RUN_ID + "_mission.stdout.log")
        with open(log_path, "w", encoding="ascii") as fh:
            fh.write("[Mission][Info][Spawn] mission start name=b5_mun_flyby "
                     "rpc=127.0.0.1:50000 stream=50001 budget=2400.000s "
                     "result=r.json\n")
        status_path = os.path.join(tmp, self.RUN_ID + "_status.json")
        with open(status_path, "w", encoding="ascii") as fh:
            fh.write(json.dumps(self._payload()))
        if stale:
            old = time.time() - 300
            os.utime(status_path, (old, old))
        return status_path

    def test_fresh_status_file_renders_machine_block(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_run(tmp)
            panel = status.render_panel(self.RUN_ID, tmp, tmp)
            self.assertIn("LIVE STATUS FILE", panel)
            self.assertIn("planAttempts:", panel)
            self.assertIn("corrBurnStarted:", panel)

    def test_stale_status_file_falls_back_to_log(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_run(tmp, stale=True)
            panel = status.render_panel(self.RUN_ID, tmp, tmp)
            self.assertNotIn("LIVE STATUS FILE", panel)

    def test_read_status_file_freshness_gate(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_run(tmp)
            now = time.time()
            data = status.read_status_file(tmp, self.RUN_ID, now=now)
            self.assertIsNotNone(data)
            self.assertEqual(data["machine"]["planAttempts"], 3)
            self.assertIsNone(status.read_status_file(
                tmp, self.RUN_ID,
                now=now + status.STATUS_FILE_FRESH_SECONDS + 1))


if __name__ == "__main__":
    unittest.main()
