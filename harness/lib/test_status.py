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

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_DIR = os.path.dirname(_HERE)
for _p in (_HARNESS_DIR, _HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
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
        self.assertEqual(parts["run"], 1)
        self.assertEqual(parts["attempt"], 1)

    def test_run_instance_ordinal_is_stripped_off_the_scenario(self):
        # A run whose id collided (two runs of one scenario inside one minute)
        # carries `_run<N>`. Without this the panel's scenario would be
        # "B5-mun-flyby_run2", load_scenario_spec would miss the spec toml, and
        # the live panel would silently lose its mission params + wall budget.
        parts = status.split_run_id("2026-07-22_1210_B5-mun-flyby_run2")
        self.assertEqual(parts["ts"], "2026-07-22_1210")
        self.assertEqual(parts["scenario"], "B5-mun-flyby")
        self.assertEqual(parts["run"], 2)
        self.assertEqual(parts["attempt"], 1)

    def test_a_collided_run_that_also_retried_splits_both_axes(self):
        parts = status.split_run_id("2026-07-22_1210_B5-mun-flyby_run2_a2")
        self.assertEqual(parts["scenario"], "B5-mun-flyby")
        self.assertEqual(parts["run"], 2)
        self.assertEqual(parts["attempt"], 2)
        self.assertIsNotNone(status.run_start_epoch("2026-07-22_1210_B5-mun-flyby_run2_a2"))

    def test_every_id_hlib_formats_round_trips_through_the_parse(self):
        # Cross-module contract: hlib.format_run_id WRITES the id, this parser
        # READS it. A change to either that the other does not follow shows up
        # here rather than as a degraded status panel mid-flight.
        for ordinal in (1, 2, 11):
            for attempt in (1, 2):
                run_id = hlib.format_run_id("2026-08-01_1628", "H23-tracking-station",
                                            attempt=attempt, ordinal=ordinal)
                parts = status.split_run_id(run_id)
                self.assertEqual("2026-08-01_1628", parts["ts"], run_id)
                self.assertEqual("H23-tracking-station", parts["scenario"], run_id)
                self.assertEqual(ordinal, parts["run"], run_id)
                self.assertEqual(attempt, parts["attempt"], run_id)


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

    def test_the_no_budget_note_distinguishes_untimed_from_key_absent(self):
        """NIT: '(no GAME budget for this phase)' was an affirmative claim that
        was FALSE for a phase in the table whose key this spec omits -- the
        machine still applies its own default there."""
        self.assertIn("untimed phase",
                      status.phase_budget_note("ORBIT", self.PARAMS))
        note = status.phase_budget_note("DOCK", self.PARAMS)
        self.assertIn("dockTimeoutSeconds", note)
        self.assertIn("applies its own default", note)
        self.assertNotIn("untimed", note)
        bad = status.phase_budget_note("PARK", {"parkTimeoutSeconds": True})
        self.assertIn("present but unreadable", bad)

    # (params builder, budget dispatcher, phase-constant prefix) per machine
    # that HAS a phase-budget dispatcher. The panel's flat table mirrors these;
    # they are the authority.
    MACHINES = (
        ("b1_params_from_dict", "_b1_phase_budget", "B1_"),
        ("eva4_params_from_dict", "_eva4_phase_budget", "EVA4_"),
        ("b2_params_from_dict", "_b2_phase_budget", "B2_"),
        ("b4_params_from_dict", "_b4_phase_budget", "B4_"),
        ("b5_params_from_dict", "_b5_phase_budget", "B5_"),
        ("bdock_params_from_dict", "_bdock_phase_budget", "BDOCK_"),
        ("forge_lko_params_from_dict", "_flko_phase_budget", "FLKO_"),
    )

    @staticmethod
    def _mlib():
        sys.path.insert(0, os.path.join(_HARNESS_DIR, "missions", "lib"))
        try:
            import mlib
        finally:
            sys.path.pop(0)
        return mlib

    @staticmethod
    def _sentinels():
        """A DISTINCT value per table key, so a mis-mapped phase reads a
        different number rather than coincidentally the right one."""
        return {key: 1000 + i for i, key in
                enumerate(sorted(set(status.PHASE_BUDGET_KEYS.values())))}

    def test_the_table_agrees_with_mlib_for_every_phase_of_every_machine(self):
        """The panel's flat PHASE_BUDGET_KEYS table is a MIRROR of mlib's
        per-machine ``_*_phase_budget`` dispatchers, which are the authority.
        Assert it against the authority, in BOTH directions and over EVERY
        phase constant each machine defines.

        This replaces a substring search over the concatenated scenario TOMLs,
        which (a) passed on a key that appeared only inside a COMMENT, (b) said
        nothing about mlib at all, and (c) never checked the REVERSE direction
        -- a phase mlib budgets that the table omits, which is precisely the G3
        bug that left B11's CAPTURE-BURN printing 'budget n/a' (2026-07-26
        review, NIT)."""
        mlib = self._mlib()
        sentinels = self._sentinels()
        checked = 0
        for builder_name, budget_name, prefix in self.MACHINES:
            builder = getattr(mlib, builder_name)
            budget_fn = getattr(mlib, budget_name)
            params = builder(dict(sentinels))
            phases = sorted({value for name, value in vars(mlib).items()
                             if name.startswith(prefix) and isinstance(value, str)})
            self.assertTrue(phases, prefix)
            for phase in phases:
                self.assertEqual(
                    budget_fn(params, phase),
                    status.phase_budget_seconds(phase, sentinels),
                    "%s phase %s: the panel table disagrees with mlib"
                    % (budget_name, phase))
                checked += 1
        self.assertGreater(checked, 50, "expected every machine's phases")

    def test_no_table_key_is_unread_by_any_machine(self):
        """The forward direction on its own: a key in the table that no
        machine's dispatcher ever returns is an INVENTED key."""
        mlib = self._mlib()
        sentinels = self._sentinels()
        produced = set()
        for builder_name, budget_name, prefix in self.MACHINES:
            params = getattr(mlib, builder_name)(dict(sentinels))
            budget_fn = getattr(mlib, budget_name)
            for name, value in vars(mlib).items():
                if not (name.startswith(prefix) and isinstance(value, str)):
                    continue
                got = budget_fn(params, value)
                if got is not None:
                    produced.add(round(float(got), 6))
        unread = sorted(
            key for key, sentinel in sentinels.items()
            if float(sentinel) not in produced
            and float(sentinel) * 2.0 not in produced)   # B-DOCK TRANSFER is 2x
        self.assertEqual(unread, [],
                         "table keys no mlib machine reads (invented keys)")


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
        self.assertIn("(57%, excl. boot)", line)
        self.assertIn("phase wall 39m39s", line)
        self.assertNotIn("telemetry-line est.", line)

    def test_falls_back_to_the_line_estimate_with_the_spec_denominator(self):
        line = status.format_wall_line(None, wall_est_total_s=600.0,
                                       wall_est_phase_s=120.0,
                                       spec_budget=4200.0)
        self.assertIn("10m00s", line)
        self.assertIn("1h10m", line)
        self.assertIn("(14%, excl. boot)", line)
        self.assertIn("telemetry-line est.", line)

    def test_the_run_budget_rides_the_line_when_known(self):
        """NIT: TWO deadlines can reap a run and _drive_mission_step holds
        both, but the panel showed only the (always smaller) mission budget."""
        line = status.format_wall_line(
            {"wallElapsedSeconds": 2379.0, "wallBudgetSeconds": 3000.0,
             "phaseWallSeconds": 100.0},
            wall_est_total_s=10.0, wall_est_phase_s=5.0, spec_budget=3000.0,
            run_budget=3500.0)
        self.assertIn("run budget 58m20s", line)

    def test_the_run_budget_term_is_omitted_when_unknown(self):
        line = status.format_wall_line(None, wall_est_total_s=600.0,
                                       wall_est_phase_s=120.0,
                                       spec_budget=4200.0, run_budget=None)
        self.assertNotIn("run budget", line)
        for bad in (float("nan"), 0.0, -1.0, True, "3500"):
            self.assertNotIn(
                "run budget",
                status.format_wall_line(None, 600.0, 120.0, 4200.0, bad), bad)

    def test_the_percentage_is_flagged_as_excluding_boot(self):
        """The mission clock starts at subprocess spawn; KSP boot and the
        pre-mission seam steps burn RUN budget before it, so the percentage
        always under-reads -- always in the 'looks safer than it is'
        direction. The line has to say so."""
        line = status.format_wall_line(
            {"wallElapsedSeconds": 100.0, "wallBudgetSeconds": 200.0},
            wall_est_total_s=0.0, wall_est_phase_s=0.0, spec_budget=200.0)
        self.assertIn("excl. boot", line)

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

    # Warp-ARMING command counts as MEASURED in the archive; the marker gates on
    # them because ratio + wall alone marked only false positives (MINOR-5).
    THRASH_ARMED = 3603      # B12 flight 2's thrashing coast
    HEALTHY_ARMED = 0        # every false positive: MJ-ASCENT / TRANSFER-BURN /
                             # CAPTURE-BURN issued zero, PARK issued one CANCEL

    def test_healthy_warping_coast_is_not_marked(self):
        self.assertFalse(status.is_low_throughput_phase(7510.0, 25.9, 4))
        self.assertFalse(status.is_low_throughput_phase(333.0, 25.5, 4))

    def test_the_measured_thrash_is_marked(self):
        # B12 flight 2: ~40 game-s per wall-s over the whole remaining budget,
        # with 3,603 warp commands issued -- warp was ARMED and did not happen.
        self.assertTrue(
            status.is_low_throughput_phase(40.0, 2400.0, self.THRASH_ARMED))

    def test_the_measured_false_positives_are_no_longer_marked(self):
        """MINOR-5, the whole point: on a healthy B11 the ratio-only rule marked
        MJ-ASCENT (1.33 / 199 s), TRANSFER-BURN (12.3 / 129 s), CAPTURE-BURN
        (8.0 / 642 s) and PARK (1.0 / 180 s) -- four rows, ZERO true positives.
        All four issued no warp-ARMING command."""
        for ratio, wall in ((1.33, 198.8), (12.3, 129.0), (7.96, 642.1),
                            (1.0, 180.4)):
            self.assertFalse(
                status.is_low_throughput_phase(ratio, wall, self.HEALTHY_ARMED),
                "ratio=%s wall=%s" % (ratio, wall))

    def test_a_cancel_only_phase_is_not_marked(self):
        """PARK's single warp command is `set_rails_warp value=0.000`, a cancel
        to 1x. Counting it as arming would keep the false positive alive."""
        self.assertFalse(
            status.is_warp_arming_action(status.ACTION_SET_RAILS_WARP, "0.000"))
        self.assertFalse(status.is_low_throughput_phase(1.0, 180.4, 0))

    def test_healthy_correction_burn_is_indistinguishable_by_ratio_alone(self):
        """The decisive number: healthy CORRECTION-BURN reads 43.6 while the
        defect reads ~40. Only the arming count separates them."""
        self.assertFalse(status.is_low_throughput_phase(43.6, 200.0, 0))
        self.assertTrue(status.is_low_throughput_phase(43.6, 200.0, 12))

    def test_cheap_waypoints_are_never_marked(self):
        # CIRCULARIZE / ORBIT / PLAN-* all read ~1.0 over ~0.5 s wall.
        self.assertFalse(status.is_low_throughput_phase(1.0, 0.5, 5))
        self.assertFalse(status.is_low_throughput_phase(0.5, 1.0, 5))

    def test_wall_boundary(self):
        floor = status.LOW_THROUGHPUT_MIN_WALL_SECONDS
        self.assertTrue(status.is_low_throughput_phase(1.0, floor, 1))
        self.assertFalse(status.is_low_throughput_phase(1.0, floor - 0.1, 1))

    def test_ratio_boundary(self):
        ceiling = status.LOW_THROUGHPUT_RATIO
        self.assertFalse(status.is_low_throughput_phase(ceiling, 500.0, 1))
        self.assertTrue(status.is_low_throughput_phase(ceiling - 0.1, 500.0, 1))

    def test_arming_boundary(self):
        floor = status.LOW_THROUGHPUT_MIN_ARMED_WARP_COMMANDS
        self.assertTrue(status.is_low_throughput_phase(1.0, 500.0, floor))
        self.assertFalse(status.is_low_throughput_phase(1.0, 500.0, floor - 1))

    def test_none_inputs_never_mark(self):
        self.assertFalse(status.is_low_throughput_phase(None, 500.0, 5))
        self.assertFalse(status.is_low_throughput_phase(1.0, None, 5))

    def test_unreadable_arming_count_never_marks(self):
        for bad in (None, "3", True, 1.5):
            self.assertFalse(status.is_low_throughput_phase(1.0, 500.0, bad), bad)

    def test_the_arming_rule_matches_mlib(self):
        """status.py is stdlib-only and cannot import mlib at runtime, so the
        action-kind constants and the arming rule are duplicated. Pin them."""
        sys.path.insert(0, os.path.join(_HARNESS_DIR, "missions", "lib"))
        try:
            import mlib
        finally:
            sys.path.pop(0)
        self.assertEqual(status.ACTION_WARP_TO_UT, mlib.ACTION_WARP_TO_UT)
        self.assertEqual(status.ACTION_SET_RAILS_WARP, mlib.ACTION_SET_RAILS_WARP)
        self.assertEqual(status.ACTION_CANCEL_WARP, mlib.ACTION_CANCEL_WARP)
        for kind, raw, typed in (
                (mlib.ACTION_WARP_TO_UT, "500.000", 500.0),
                (mlib.ACTION_CANCEL_WARP, "None", None),
                (mlib.ACTION_SET_RAILS_WARP, "0.000", 0.0),
                (mlib.ACTION_SET_RAILS_WARP, "3.000", 3.0)):
            self.assertEqual(status.is_warp_arming_action(kind, raw),
                             mlib.is_warp_arming_command(kind, typed),
                             "%s %s" % (kind, raw))


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
        MEASURED: that phase ran ~42 game-s per wall-s over an hour of wall
        while re-issuing its own warp thousands of times."""
        lines = ["[Mission][Info][COAST-TO-TARGET] phase X -> COAST-TO-TARGET "
                 "ut=74228.0 alt=1 ap=1 vsurf=1"]
        # 200 samples (~200 wall s) advancing UT by 42 s each => ratio ~42,
        # each one re-arming the native warp (the thrash's signature).
        for i in range(200):
            lines.append(_telem("COAST-TO-TARGET", tts="nan",
                                ut="%0.3f" % (74228.0 + 42.0 * (i + 1))))
            lines.append("[Mission][Info][COAST-TO-TARGET] action warp_to_ut "
                         "value=99999.000")
        rows = status.build_phase_rows(status.summarize_mission_lines(lines))
        open_row = rows[-1]
        self.assertIsNone(open_row["game_s"])          # still an open span
        self.assertAlmostEqual(open_row["ratio"], 42.0, places=0)
        self.assertEqual(open_row["armed_warp_cmds"], 200)
        self.assertTrue(open_row["low"])

    def test_a_long_1x_phase_that_never_armed_warp_is_not_marked(self):
        """MINOR-5: PARK's 180 s dwell at 1x is deliberate, and its ONE warp
        command is `set_rails_warp value=0.000` -- a CANCEL to 1x. The
        ratio-only rule marked it on every healthy run; the arming gate does
        not."""
        lines = ["[Mission][Info][PARK] phase X -> PARK ut=0.0 alt=1 ap=1 "
                 "vsurf=1",
                 "[Mission][Info][PARK] action set_rails_warp value=0.000"]
        lines += [_telem("PARK", tts="nan") for _ in range(200)]
        lines += ["[Mission][Info][ORBIT-COMMIT] phase PARK -> ORBIT-COMMIT "
                  "ut=200.0 alt=1 ap=1 vsurf=1"]
        rows = status.build_phase_rows(status.summarize_mission_lines(lines))
        park = [r for r in rows if r["phase"] == "PARK"][0]
        self.assertAlmostEqual(park["ratio"], 1.0)
        self.assertEqual(park["armed_warp_cmds"], 0)
        self.assertFalse(park["low"])

    def test_a_long_1x_phase_that_DID_arm_warp_is_marked(self):
        lines = ["[Mission][Info][PARK] phase X -> PARK ut=0.0 alt=1 ap=1 "
                 "vsurf=1",
                 "[Mission][Info][PARK] action set_rails_warp value=4.000"]
        lines += [_telem("PARK", tts="nan") for _ in range(200)]
        lines += ["[Mission][Info][ORBIT-COMMIT] phase PARK -> ORBIT-COMMIT "
                  "ut=200.0 alt=1 ap=1 vsurf=1"]
        rows = status.build_phase_rows(status.summarize_mission_lines(lines))
        park = [r for r in rows if r["phase"] == "PARK"][0]
        self.assertEqual(park["armed_warp_cmds"], 1)
        self.assertTrue(park["low"])

    def test_arming_commands_are_attributed_to_their_own_phase(self):
        lines = ["[Mission][Info][A] phase X -> A ut=0.0 alt=1 ap=1 vsurf=1",
                 "[Mission][Info][A] action warp_to_ut value=10.000",
                 _telem("A", tts="nan"),
                 "[Mission][Info][B] phase A -> B ut=10.0 alt=1 ap=1 vsurf=1",
                 "[Mission][Info][B] action cancel_warp value=nan",
                 _telem("B", tts="nan")]
        rows = status.build_phase_rows(status.summarize_mission_lines(lines))
        by_phase = {r["phase"]: r for r in rows}
        self.assertEqual(by_phase["A"]["armed_warp_cmds"], 1)
        self.assertEqual(by_phase["B"]["armed_warp_cmds"], 0)


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
                                          "warpCommands": 3603,
                                          "armedWarpCommands": 3603}})
            with open(path, "w", encoding="ascii") as fh:
                fh.write(json.dumps(payload))
            panel = status.render_panel(self.RUN_ID, tmp, tmp)
            self.assertIn("mission wall: 39m39s / 1h10m (57%, excl. boot)", panel)
            self.assertIn("phase wall 39m39s", panel)
            self.assertNotIn("telemetry-line est.", panel)
            # The live open-phase ratio: this is the measured thrash number,
            # and it ARMED warp 3,603 times, so the marker fires.
            self.assertIn("40 game-s per wall-s", panel)
            self.assertIn("3603 armed", panel)
            self.assertIn("<- LOW", panel)

    def test_the_throughput_block_names_the_phase_its_numbers_came_from(self):
        """MINOR-6: the header phase comes from the LOG's last transition and
        the throughput numbers come from the STATUS FILE's phase. A real render
        showed 'PHASE: EVA-WINDOW' over DESCENT's numbers with neither
        labelled, and the operator read one as the other."""
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write_run(tmp)
            payload = self._payload()
            payload["phaseWarp"] = {"phase": "DESCENT", "wallSeconds": 61.1,
                                    "gameSeconds": 61.1,
                                    "gameSecondsPerWallSecond": 1.0,
                                    "warpCommands": 0, "armedWarpCommands": 0}
            with open(path, "w", encoding="ascii") as fh:
                fh.write(json.dumps(payload))
            panel = status.render_panel(self.RUN_ID, tmp, tmp)
            # The log here has no transition, so the header phase is PRELAUNCH.
            self.assertIn("PHASE: PRELAUNCH", panel)
            self.assertIn("status-file phase DESCENT, NOT the PRELAUNCH header",
                          panel)

    def test_the_throughput_block_is_unlabelled_when_the_phases_agree(self):
        line = status.format_phase_throughput_line(
            {"phase": "PARK", "wallSeconds": 180.0, "gameSeconds": 180.0,
             "gameSecondsPerWallSecond": 1.0, "warpCommands": 1,
             "armedWarpCommands": 0}, "PARK")
        self.assertTrue(line.startswith("phase throughput: "))
        self.assertNotIn("NOT the", line)
        self.assertNotIn("<- LOW", line)   # 1 cancel, 0 armed -> no marker

    def test_no_throughput_block_without_a_payload(self):
        self.assertIsNone(status.format_phase_throughput_line(None, "PARK"))
        self.assertIsNone(status.format_phase_throughput_line("nope", "PARK"))

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
