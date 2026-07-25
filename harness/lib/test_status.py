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
           warp="NONEx1.000", thr=0.0, warp_to="nan", ut=None):
    """A telemetry line. ``ut`` appends the Phase-2 trailing ut= token; omitted
    (the default) it reproduces a Phase-1 line verbatim, so every pre-existing
    caller's expectations are unchanged."""
    return ("[Mission][VerboseRateLimited][%s] telemetry ap=1.0 pe=1.0 "
            "ecc=0.5 inc=0.1 alt=100.0 vspd=1.0 body=Kerbin nodes=%d "
            "nodeDv=%s nodeUt=nan tts=%s warpTo=%s lf=10.0 thr=%.3f "
            "situation=ORBITING warp=%s apErr=nan%s"
            % (phase, nodes, node_dv, tts, warp_to, thr, warp,
               ("" if ut is None else " ut=%s" % ut)))


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

    # The B12 ORBIT-tail params, verbatim from scenarios/B12-minmus-orbit.toml.
    ORBIT_PARAMS = {"capturePlanTimeoutSeconds": 300,
                    "captureBurnTimeoutSeconds": 200000,
                    "parkTimeoutSeconds": 600,
                    "commitTimeoutSeconds": 300}

    # The B13/B14 LANDING-tail params, verbatim from
    # scenarios/B13-mun-landing.toml.
    LANDING_PARAMS = {"descentTimeoutSeconds": 3000,
                      "landedTimeoutSeconds": 600,
                      "commitTimeoutSeconds": 300,
                      "landedDwellSeconds": 120}

    def test_plan_phases_share_plan_timeout(self):
        self.assertEqual(
            status.phase_budget_seconds("PLAN-CORRECTION", self.PARAMS), 300)
        self.assertEqual(
            status.phase_budget_seconds("PLAN-TRANSFER", self.PARAMS), 300)

    def test_untimed_phase_is_none(self):
        self.assertIsNone(status.phase_budget_seconds("RETURN", self.PARAMS))
        self.assertIsNone(status.phase_budget_seconds("ORBIT", self.PARAMS))

    def test_orbit_tail_phases_resolve(self):
        """G3: these four printed 'budget n/a' -- including CAPTURE-BURN, the
        single most expensive phase in the suite (B11 MEASURED 642 wall s)."""
        self.assertEqual(
            status.phase_budget_seconds("PLAN-CAPTURE", self.ORBIT_PARAMS), 300)
        self.assertEqual(
            status.phase_budget_seconds("CAPTURE-BURN", self.ORBIT_PARAMS),
            200000)
        self.assertEqual(
            status.phase_budget_seconds("PARK", self.ORBIT_PARAMS), 600)
        self.assertEqual(
            status.phase_budget_seconds("ORBIT-COMMIT", self.ORBIT_PARAMS), 300)

    def test_landing_tail_phases_resolve(self):
        """The B13/B14 tail. DESCENT deliberately REUSES the B1/EVA-4 row (that
        is why the landing phase is NAMED DESCENT and its param
        descentTimeoutSeconds -- the status table, the warp audit and every log
        grep already know that name)."""
        self.assertEqual(
            status.phase_budget_seconds("DESCENT", self.LANDING_PARAMS), 3000)
        self.assertEqual(
            status.phase_budget_seconds("LANDED-SETTLE", self.LANDING_PARAMS),
            600)
        self.assertEqual(
            status.phase_budget_seconds("SURFACE-COMMIT", self.LANDING_PARAMS),
            300)
        self.assertIsNone(
            status.phase_budget_seconds("SURFACE-COMMITTED",
                                        self.LANDING_PARAMS))

    def test_ascent_aliases_share_one_key(self):
        """The flat table is only sound because colliding phase names map to
        the SAME param key in every machine (mlib's _*_phase_budget)."""
        for phase in ("ASCENT", "MJ-ASCENT", "STATION-ASCENT", "INT-ASCENT"):
            self.assertEqual(status.phase_budget_seconds(phase, self.PARAMS),
                             420, phase)

    def test_bdock_transfer_carries_the_2x_multiplier(self):
        """mlib._bdock_phase_budget returns 2 * transfer_timeout (TRANSFER runs
        two transfers); the panel must not show half the real budget."""
        params = {"transferTimeoutSeconds": 120}
        self.assertEqual(status.phase_budget_seconds("TRANSFER", params), 240.0)

    def test_key_present_in_table_but_absent_from_params_is_none(self):
        self.assertIsNone(status.phase_budget_seconds("DOCK", self.PARAMS))

    def test_bool_is_not_a_budget(self):
        self.assertIsNone(
            status.phase_budget_seconds("PARK", {"parkTimeoutSeconds": True}))

    def test_every_table_key_is_defined_by_a_committed_spec(self):
        """No invented keys: every budget key in the table must appear in at
        least one committed scenarios/*.toml."""
        scen_dir = os.path.join(_HARNESS_DIR, "scenarios")
        blob = ""
        for name in sorted(os.listdir(scen_dir)):
            if name.endswith(".toml"):
                with open(os.path.join(scen_dir, name), "r",
                          encoding="utf-8", errors="replace") as fh:
                    blob += fh.read()
        missing = sorted({k for k in status.PHASE_BUDGET_KEYS.values()
                          if k not in blob})
        self.assertEqual(missing, [], "budget keys no committed spec defines")


class ScenarioSpecReadTests(unittest.TestCase):
    """G1: the WALL denominator lives in driver.steps[].budget, NOT in
    [driver.missionParams] -- which is why the panel could show a wall number
    with no scale beside it."""

    SPEC = {"driver": {
        "steps": [{"cmd": "LoadGame", "expect": "OK", "budget": 240},
                  {"cmd": "SetSetting", "expect": "OK"},
                  {"phase": "mission", "expect": "MISSION-OK", "budget": 4200},
                  {"cmd": "FlushAndQuit", "expect": "OK"}],
        "missionParams": {"ascentTimeoutSeconds": 420}}}

    def test_mission_wall_budget_is_the_mission_step_budget(self):
        self.assertEqual(status.mission_wall_budget_seconds(self.SPEC), 4200.0)

    def test_mission_params_still_read(self):
        self.assertEqual(
            status.mission_params_from_spec(self.SPEC)["ascentTimeoutSeconds"],
            420)

    def test_seam_only_spec_has_no_mission_wall_budget(self):
        spec = {"driver": {"steps": [{"cmd": "LoadGame", "budget": 240}]}}
        self.assertIsNone(status.mission_wall_budget_seconds(spec))

    def test_missing_or_malformed_spec_is_none(self):
        self.assertIsNone(status.mission_wall_budget_seconds({}))
        self.assertIsNone(status.mission_wall_budget_seconds(
            {"driver": {"steps": "not-a-list"}}))
        self.assertIsNone(status.mission_wall_budget_seconds(
            {"driver": {"steps": [{"phase": "mission"}]}}))

    def test_real_b12_spec_reads_4200(self):
        spec = status.load_scenario_spec(
            "B12-minmus-orbit", os.path.join(_HARNESS_DIR, "scenarios"))
        if not spec:
            self.skipTest("tomllib unavailable (py < 3.11)")
        self.assertEqual(status.mission_wall_budget_seconds(spec), 4200.0)
        self.assertEqual(
            status.phase_budget_seconds("CAPTURE-BURN",
                                        status.mission_params_from_spec(spec)),
            200000)


class WallLineTests(unittest.TestCase):
    """G1: the panel printed 'wall ~N (telemetry-line est.)' with NO
    denominator, so a run 57% through its WALL budget looked fine next to GAME
    budgets reading ~7.5% consumed."""

    def test_real_numbers_from_the_status_file_win(self):
        line = status.format_wall_line(
            {"wallElapsedSeconds": 2379.0, "wallBudgetSeconds": 4200.0,
             "phaseWallSeconds": 2379.0},
            wall_est_total_s=10.0, wall_est_phase_s=5.0, spec_budget=4200.0)
        self.assertIn("39m39s", line)
        self.assertIn("1h10m", line)
        self.assertIn("(57%)", line)
        self.assertIn("phase wall 39m39s", line)
        self.assertNotIn("telemetry-line est.", line)

    def test_falls_back_to_the_line_estimate_with_the_spec_denominator(self):
        line = status.format_wall_line(None, wall_est_total_s=600.0,
                                       wall_est_phase_s=120.0,
                                       spec_budget=4200.0)
        self.assertIn("10m00s", line)
        self.assertIn("1h10m", line)
        self.assertIn("(14%)", line)
        self.assertIn("telemetry-line est.", line)

    def test_older_status_file_without_wall_fields_falls_back(self):
        line = status.format_wall_line({"machine": {}, "snapshot": {}},
                                       wall_est_total_s=60.0,
                                       wall_est_phase_s=30.0,
                                       spec_budget=None)
        self.assertIn("budget n/a", line)
        self.assertIn("telemetry-line est.", line)

    def test_non_finite_status_values_are_ignored(self):
        line = status.format_wall_line(
            {"wallElapsedSeconds": float("nan"), "wallBudgetSeconds": 4200.0},
            wall_est_total_s=60.0, wall_est_phase_s=30.0, spec_budget=None)
        self.assertIn("telemetry-line est.", line)
        self.assertIn("1h10m", line)


class ThroughputMarkerTests(unittest.TestCase):
    """G2: gameSecondsPerWallSecond is THE number that named two shared-machine
    warp defects, and it had zero programmatic consumers. Measured references
    are in the status.py threshold block."""

    def test_ratio_is_game_over_wall(self):
        self.assertAlmostEqual(
            status.phase_throughput_ratio(194493.0, 25.9), 7509.4, places=0)

    def test_ratio_none_for_open_or_zero_spans(self):
        self.assertIsNone(status.phase_throughput_ratio(None, 10.0))
        self.assertIsNone(status.phase_throughput_ratio(100.0, 0.0))
        self.assertIsNone(status.phase_throughput_ratio(float("nan"), 10.0))

    def test_healthy_warping_coast_is_not_marked(self):
        self.assertFalse(status.is_low_throughput_phase(7510.0, 25.9))
        self.assertFalse(status.is_low_throughput_phase(333.0, 25.5))

    def test_the_measured_thrash_is_marked(self):
        # B12 flight 2: ~40 game-s per wall-s over the whole remaining budget.
        self.assertTrue(status.is_low_throughput_phase(40.0, 2400.0))

    def test_legitimate_1x_holds_mark_too_and_that_is_intended(self):
        # B11 CAPTURE-BURN (MechJeb's deliberate 600 s pre-ignition hold) and
        # PARK's 180 s dwell. The marker is informational, not a failure -- the
        # ratio alone CANNOT separate them from the thrash (which reads HIGHER).
        self.assertTrue(status.is_low_throughput_phase(7.96, 642.0))
        self.assertTrue(status.is_low_throughput_phase(1.0, 180.4))

    def test_cheap_waypoints_are_never_marked(self):
        # CIRCULARIZE / ORBIT / PLAN-* all read ~1.0 over ~0.5 s wall.
        self.assertFalse(status.is_low_throughput_phase(1.0, 0.5))
        self.assertFalse(status.is_low_throughput_phase(0.5, 1.0))

    def test_wall_boundary(self):
        floor = status.LOW_THROUGHPUT_MIN_WALL_SECONDS
        self.assertTrue(status.is_low_throughput_phase(1.0, floor))
        self.assertFalse(status.is_low_throughput_phase(1.0, floor - 0.1))

    def test_ratio_boundary(self):
        ceiling = status.LOW_THROUGHPUT_RATIO
        self.assertFalse(status.is_low_throughput_phase(ceiling, 500.0))
        self.assertTrue(status.is_low_throughput_phase(ceiling - 0.1, 500.0))

    def test_none_inputs_never_mark(self):
        self.assertFalse(status.is_low_throughput_phase(None, 500.0))
        self.assertFalse(status.is_low_throughput_phase(1.0, None))


class MachineBlockReadTests(unittest.TestCase):
    """G3: the ORBIT-tail heuristics read the machine block, which arrives in
    TWO shapes -- the status file's decoded dict and the log line's raw kv
    strings ('-' = field absent on this machine, 'none' = present but unset)."""

    def test_decoded_dict_values(self):
        m = {"captureExecDownStreak": 3, "parkEverStable": True,
             "commitResult": "OK", "minNodeDv": None}
        self.assertEqual(status.machine_number(m, "captureExecDownStreak"), 3.0)
        self.assertTrue(status.machine_flag(m, "parkEverStable"))
        self.assertIsNone(status.machine_number(m, "minNodeDv"))
        self.assertIsNone(status.machine_number(m, "absentKey"))

    def test_raw_log_tokens(self):
        m = {"parkStableStreak": "3", "parkEverStable": "True",
             "sepSettleStreak": "-", "minNodeDv": "none"}
        self.assertEqual(status.machine_number(m, "parkStableStreak"), 3.0)
        self.assertTrue(status.machine_flag(m, "parkEverStable"))
        self.assertIsNone(status.machine_number(m, "sepSettleStreak"))
        self.assertIsNone(status.machine_number(m, "minNodeDv"))

    def test_bool_is_not_a_number(self):
        self.assertIsNone(status.machine_number({"x": True}, "x"))

    def test_machine_line_is_captured_from_the_log(self):
        lines = [
            "[Mission][VerboseRateLimited][PARK] machine phase=PARK rounds=2 "
            "parkStableStreak=3 parkEverStable=True captureExecDownStreak=0 "
            "commitResult= sepSettleStreak=-",
        ]
        summary = status.summarize_mission_lines(lines)
        self.assertEqual(summary["machine"]["parkStableStreak"], "3")
        self.assertEqual(summary["machine"]["phase"], "PARK")
        # A machine line is state, not a sparse event.
        self.assertEqual(summary["events"], [])


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


class OrbitTailHeuristicTests(unittest.TestCase):
    """G3: the four ORBIT-tail phases had NO heuristic branch at all, so the
    most expensive phase in the suite rendered a bare 'PHASE: budget n/a.'."""

    PARAMS = {"capturePlanTimeoutSeconds": 300,
              "captureBurnTimeoutSeconds": 200000,
              "parkTimeoutSeconds": 600, "commitTimeoutSeconds": 300,
              "parkDwellSeconds": 180}

    def _at(self, phase, prev="X", ut=1000.0, **telem):
        return ["[Mission][Info][%s] phase %s -> %s ut=%s alt=1 ap=1 vsurf=1"
                % (phase, prev, phase, ut),
                _telem(phase, tts="nan", **telem)]

    def test_capture_burn_pre_ignition_is_named_not_a_hang(self):
        lines = self._at("CAPTURE-BURN")
        lines[-1] = lines[-1] + " nodeExec=1 ttPe=900.0"
        summary = status.summarize_mission_lines(lines)
        line = status.derive_heuristic(summary, self.PARAMS,
                                       machine={"captureExecDownStreak": 0})
        self.assertIn("NOT a hang", line)
        self.assertIn("nodeExec=1", line)
        self.assertIn("600 GAME seconds", line)

    def test_capture_burn_executor_down_streak_is_named(self):
        summary = status.summarize_mission_lines(self._at("CAPTURE-BURN"))
        line = status.derive_heuristic(
            summary, self.PARAMS,
            machine={"captureExecDownStreak": 4, "captureExecReissues": 1})
        self.assertIn("read DOWN for 4", line)
        self.assertIn("reissues=1", line)

    def test_park_names_the_debounce_and_the_deliberate_1x_hold(self):
        summary = status.summarize_mission_lines(self._at("PARK"))
        line = status.derive_heuristic(
            summary, self.PARAMS,
            machine={"parkStableStreak": 3, "parkEverStable": True})
        self.assertIn("debounce at 3", line)
        self.assertIn("BY DESIGN", line)
        self.assertIn("180", line)

    def test_plan_capture_names_the_window_countdown(self):
        summary = status.summarize_mission_lines(self._at("PLAN-CAPTURE"))
        line = status.derive_heuristic(
            summary, self.PARAMS,
            machine={"captureNodeBadStreak": 2, "captureReplans": 1})
        self.assertIn("NOT at periapsis", line)
        self.assertIn("PLAN-CAPTURE", line)

    def test_orbit_commit_reports_the_seam_result(self):
        summary = status.summarize_mission_lines(self._at("ORBIT-COMMIT"))
        self.assertIn("polling the response channel",
                      status.derive_heuristic(summary, self.PARAMS,
                                              machine={"commitResult": ""}))
        self.assertIn("returned OK",
                      status.derive_heuristic(summary, self.PARAMS,
                                              machine={"commitResult": "OK"}))

    def test_descent_is_landing_aware_only_when_the_machine_fields_exist(self):
        """DESCENT is SHARED with B1 / EVA-4 / B4's suborbital lane, so the
        landing branch must key off the landing machine fields, not the phase
        name. A B1 descent has neither and must keep its generic line."""
        summary = status.summarize_mission_lines(self._at("DESCENT"))
        generic = status.derive_heuristic(summary, self.PARAMS, machine={})
        self.assertNotIn("LandingAutopilot", generic)
        self.assertNotIn("MechJeb OWNS the warp", generic)
        landing = status.derive_heuristic(
            summary, dict(self.PARAMS, descentTimeoutSeconds=3000),
            machine={"landingEngaged": True, "landingApDownStreak": 0,
                     "landingAltRef": 141000.0})
        self.assertIn("MechJeb OWNS the warp", landing)
        self.assertIn("engaged=True", landing)

    def test_descent_names_the_observed_autopilot_down_streak(self):
        summary = status.summarize_mission_lines(self._at("DESCENT"))
        line = status.derive_heuristic(
            summary, self.PARAMS,
            machine={"landingEngaged": True, "landingApDownStreak": 2,
                     "landingApReissues": 1})
        self.assertIn("read DOWN for 2", line)
        self.assertIn("landing-autopilot-not-enabled", line)

    def test_landed_settle_names_the_dwell_and_the_body(self):
        summary = status.summarize_mission_lines(self._at("LANDED-SETTLE"))
        line = status.derive_heuristic(
            summary, dict(self.PARAMS, landedDwellSeconds=120),
            machine={"landedStableStreak": 3, "landedEverStable": True,
                     "landedBody": "Mun"})
        self.assertIn("touched down on Mun", line)
        self.assertIn("debounce at 3", line)
        self.assertIn("recorded surface coverage", line)

    def test_surface_commit_reports_the_seam_result(self):
        summary = status.summarize_mission_lines(self._at("SURFACE-COMMIT"))
        self.assertIn("polling the response channel",
                      status.derive_heuristic(summary, self.PARAMS,
                                              machine={"commitResult": ""}))
        self.assertIn("returned OK",
                      status.derive_heuristic(summary, self.PARAMS,
                                              machine={"commitResult": "OK"}))

    def test_machine_falls_back_to_the_log_line_when_no_status_file(self):
        lines = self._at("PARK") + [
            "[Mission][VerboseRateLimited][PARK] machine phase=PARK "
            "parkStableStreak=3 parkEverStable=True"]
        summary = status.summarize_mission_lines(lines)
        line = status.derive_heuristic(summary, self.PARAMS)  # machine=None
        self.assertIn("debounce at 3", line)


class PhaseRowRatioTests(unittest.TestCase):
    """G2: the phase-history rows stopped one division short of the number
    that named the defect."""

    def _lines(self):
        # A 250 game-second phase sampled twice (~2 wall s) => ratio 125.
        return [
            "[Mission][Info][MJ-ASCENT] phase PRELAUNCH -> MJ-ASCENT "
            "ut=100.0 alt=1 ap=1 vsurf=1",
            _telem("MJ-ASCENT", tts="nan"),
            _telem("MJ-ASCENT", tts="nan"),
            "[Mission][Info][CIRCULARIZE] phase MJ-ASCENT -> CIRCULARIZE "
            "ut=350.0 alt=1 ap=1 vsurf=1",
            _telem("CIRCULARIZE", tts="nan"),
        ]

    def test_rows_carry_the_ratio(self):
        rows = status.build_phase_rows(
            status.summarize_mission_lines(self._lines()))
        ascent = rows[1]
        self.assertAlmostEqual(ascent["ratio"], 125.0)
        self.assertFalse(ascent["low"])  # only 2 s of wall

    def test_zero_wall_and_unestimable_open_rows_have_no_ratio(self):
        rows = status.build_phase_rows(
            status.summarize_mission_lines(self._lines()))
        self.assertIsNone(rows[0]["ratio"])   # synthetic PRELAUNCH, 0 wall
        # The open row here has one sample and no ut= token, so the game-span
        # estimator cannot resolve it.
        self.assertIsNone(rows[-1]["ratio"])
        self.assertIsNone(rows[-1]["game_s"])

    def test_open_row_carries_an_estimated_ratio_and_marks_the_thrash(self):
        """The OPEN phase is the one an operator is staring at, and it is
        exactly where the measured B12 coast thrash hid behind 'ratio n/a'.
        MEASURED: that phase ran ~42 game-s per wall-s over an hour of wall."""
        lines = ["[Mission][Info][COAST-TO-TARGET] phase X -> COAST-TO-TARGET "
                 "ut=74228.0 alt=1 ap=1 vsurf=1"]
        # 200 samples (~200 wall s) advancing UT by 42 s each => ratio ~42.
        for i in range(200):
            lines.append(_telem("COAST-TO-TARGET", tts="nan",
                                ut="%0.3f" % (74228.0 + 42.0 * (i + 1))))
        rows = status.build_phase_rows(status.summarize_mission_lines(lines))
        open_row = rows[-1]
        self.assertIsNone(open_row["game_s"])          # still an open span
        self.assertAlmostEqual(open_row["ratio"], 42.0, places=0)
        self.assertTrue(open_row["low"])

    def test_a_long_1x_phase_is_marked_low(self):
        lines = ["[Mission][Info][PARK] phase X -> PARK ut=0.0 alt=1 ap=1 "
                 "vsurf=1"]
        lines += [_telem("PARK", tts="nan") for _ in range(200)]
        lines += ["[Mission][Info][ORBIT-COMMIT] phase PARK -> ORBIT-COMMIT "
                  "ut=200.0 alt=1 ap=1 vsurf=1"]
        rows = status.build_phase_rows(status.summarize_mission_lines(lines))
        park = [r for r in rows if r["phase"] == "PARK"][0]
        self.assertAlmostEqual(park["ratio"], 1.0)
        self.assertTrue(park["low"])


class OptInTelemetryTailTests(unittest.TestCase):
    """The opt-in nodeExec= / ttPe= tail the mission appends only when the
    channel was read; the CAPTURE-BURN heuristic reads nodeExec."""

    def test_tail_parsed_when_present(self):
        telem = status.parse_telemetry_message(
            "telemetry ap=1 pe=2 ecc=0 inc=0 alt=5 vspd=0 body=Minmus nodes=0 "
            "nodeDv=nan nodeUt=nan tts=nan warpTo=nan lf=1 thr=0 "
            "situation=ORBITING warp=NONEx1.000 apErr=nan ut=10.0 nodeExec=1 "
            "ttPe=275.822")
        self.assertEqual(telem["nodeExec"], 1.0)
        self.assertAlmostEqual(telem["ttPe"], 275.822)

    def test_landing_tail_parsed_when_present(self):
        """The B13/B14 opt-ins ride the SAME opt_token mechanism; the DESCENT
        heuristic reads landAP and the settled gate reads hspd."""
        telem = status.parse_telemetry_message(
            "telemetry ap=1 pe=2 ecc=0 inc=0 alt=5 vspd=-1 body=Mun nodes=0 "
            "nodeDv=nan nodeUt=nan tts=nan warpTo=nan lf=1 thr=0 "
            "situation=LANDED warp=NONEx1.000 apErr=nan ut=10.0 landAP=1 "
            "hspd=0.250")
        self.assertEqual(telem["landAP"], 1.0)
        self.assertAlmostEqual(telem["hspd"], 0.25)

    def test_absent_tail_is_nan_not_an_error(self):
        telem = status.parse_telemetry_message(REAL_TELEMETRY.split("] ", 2)[-1])
        self.assertTrue(math.isnan(telem["nodeExec"]))
        self.assertTrue(math.isnan(telem["ttPe"]))
        self.assertTrue(math.isnan(telem["landAP"]))
        self.assertTrue(math.isnan(telem["hspd"]))


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

    def test_fresh_status_file_renders_the_real_wall_line(self):
        """G1 end-to-end: the panel prefers the mission's MEASURED wall numbers
        over the telemetry-line estimate and shows the denominator."""
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write_run(tmp)
            payload = self._payload()
            payload.update({"wallElapsedSeconds": 2379.0,
                            "wallRemainingSeconds": 1821.0,
                            "wallBudgetSeconds": 4200.0,
                            "phaseWallSeconds": 2379.0,
                            "phaseWarp": {"phase": "PLAN-CORRECTION",
                                          "wallSeconds": 2379.0,
                                          "gameSeconds": 95160.0,
                                          "gameSecondsPerWallSecond": 40.0,
                                          "warpCommands": 3603}})
            with open(path, "w", encoding="ascii") as fh:
                fh.write(json.dumps(payload))
            panel = status.render_panel(self.RUN_ID, tmp, tmp)
            self.assertIn("mission wall: 39m39s / 1h10m (57%)", panel)
            self.assertIn("phase wall 39m39s", panel)
            self.assertNotIn("telemetry-line est.", panel)
            # The live open-phase ratio: this is the measured thrash number.
            self.assertIn("phase throughput: 40 game-s per wall-s", panel)
            self.assertIn("<- LOW", panel)

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
