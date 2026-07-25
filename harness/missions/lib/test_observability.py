"""Unit + fake-flight tests for the live-observability instrumentation
(docs/dev/design-live-observability.md Phase 2):

  2a  mlib.format_machine_state / machine_state_dict + the rate-limited
      MACHINE-STATE line and the telemetry line's trailing ut= token,
  2b  mlib.diff_machine_state + the fly loop's loud 'gate ...' lines,
  2c  mlib.format_snapshot_compact + the event-window ring-buffer dump,
  2d  mission_runner.StatusFileWriter / status_path_for + the atomic
      results/<runId>_status.json write.

Same import bootstrap as test_shells.py (missions/ prepended for the shell
module). NO krpc, NO KSP, NO network; the only filesystem writes go to a
tempdir (the status-file atomicity tests need a real os.replace).

ASCII only; stdlib only.
"""

import json
import math
import os
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mlib                    # noqa: E402
import mission_runner          # noqa: E402
import b1_pad_hop              # noqa: E402


def _b5_state(**kw):
    params = mlib.b5_params_from_dict({
        "targetApoapsisMeters": 80000, "targetPeriapsisMeters": 80000,
        "apoErrorMeters": 5000, "periErrorMeters": 5000,
        "ascentTimeoutSeconds": 420, "circularizeTimeoutSeconds": 300,
    })
    state = mlib.b5_initial_state(params)
    from dataclasses import replace
    return replace(state, **kw) if kw else state


def _b1_state():
    params = mlib.b1_params_from_dict({
        "throttle": 1.0,
        "apoapsisWindowMeters": {"min": 6000, "max": 30000},
        "chuteDeployAltMeters": 2500,
        "landedSituations": ["LANDED"],
        "ascentTimeoutSeconds": 90, "coastTimeoutSeconds": 180,
        "descentTimeoutSeconds": 240,
    })
    return mlib.b1_initial_state(params)


class MachineStateFormatTests(unittest.TestCase):
    def test_b5_line_carries_all_decision_fields(self):
        state = _b5_state(correction_rounds_done=1, plan_attempts=2,
                          body_blank_count=3, corr_burn_started=True,
                          aligned_streak=2, min_node_dv=12.5, warp_cmd=6,
                          phys_warp_cmd=1, warp_to_cmd=14946.5,
                          planned_node_count=1, burn_static_since=100.0)
        line = mlib.format_machine_state(state, ut=160.0)
        self.assertTrue(line.startswith("machine phase=PRELAUNCH"))
        for token in ("rounds=1", "planAttempts=2", "bodyBlank=3",
                      "corrBurnStarted=True", "alignedStreak=2",
                      "minNodeDv=12.500", "warpCmd=6", "physWarpCmd=1",
                      "warpToCmd=14946.500", "plannedNodes=1",
                      "burnStaticAge=60.000", "frozenCount=0"):
            self.assertIn(token, line)

    def test_burn_static_age_none_while_not_static(self):
        line = mlib.format_machine_state(_b5_state(), ut=160.0)
        self.assertIn("burnStaticAge=none", line)

    def test_b1_state_renders_absent_fields_as_dash(self):
        line = mlib.format_machine_state(_b1_state(), ut=1.0)
        self.assertIn("phase=PRELAUNCH", line)
        self.assertIn("planAttempts=-", line)
        self.assertIn("corrBurnStarted=-", line)
        self.assertIn("burnStaticAge=-", line)

    def test_dict_is_json_safe(self):
        state = _b5_state(min_node_dv=None, warp_to_cmd=None)
        d = mlib.machine_state_dict(state, ut=float("nan"))
        text = json.dumps(d)  # must not need allow_nan
        self.assertNotIn("NaN", text)
        self.assertIsNone(d["burnStaticAge"])
        self.assertEqual(d["phase"], "PRELAUNCH")
        self.assertEqual(d["planAttempts"], 0)
        self.assertEqual(d["bodyBlank"], 0)

    def test_dict_burn_static_age_derived(self):
        state = _b5_state(burn_static_since=50.0)
        d = mlib.machine_state_dict(state, ut=170.0)
        self.assertAlmostEqual(d["burnStaticAge"], 120.0)


class DiffMachineStateTests(unittest.TestCase):
    def test_latch_flip_and_counter_step_reported(self):
        prev = _b5_state()
        new = _b5_state(corr_burn_started=True, plan_attempts=1,
                        aligned_streak=1, body_blank_count=1)
        changes = mlib.diff_machine_state(prev, new)
        self.assertIn("corrBurnStarted False->True", changes)
        self.assertIn("planAttempts 0->1", changes)
        self.assertIn("alignedStreak 0->1", changes)
        self.assertIn("bodyBlank 0->1", changes)

    def test_no_change_is_empty(self):
        state = _b5_state(plan_attempts=2)
        self.assertEqual(mlib.diff_machine_state(state, state), [])

    def test_warp_to_cmd_none_to_target(self):
        changes = mlib.diff_machine_state(
            _b5_state(), _b5_state(warp_to_cmd=1234.5))
        self.assertIn("warpToCmd none->1234.500", changes)

    def test_noisy_fields_are_not_diffed(self):
        prev = _b5_state()
        new = _b5_state(min_node_dv=3.0, last_plan_ut=99.0,
                        phase_entry_ut=42.0, frozen_count=5,
                        last_warp_issue_ut=77.0)
        self.assertEqual(mlib.diff_machine_state(prev, new), [])

    def test_b1_states_produce_no_changes(self):
        self.assertEqual(mlib.diff_machine_state(_b1_state(), _b1_state()), [])


class SnapshotFormatTests(unittest.TestCase):
    def test_compact_line_fields(self):
        s = mlib.TelemetrySnapshot(ut=10.5, altitude=1234.0, apoapsis=80000.0,
                                   periapsis=-100.0, body="Kerbin",
                                   node_count=1, node_dv=42.5, throttle=0.25,
                                   situation="ORBITING")
        line = mlib.format_snapshot_compact(s)
        for token in ("ut=10.500", "alt=1234.000", "body=Kerbin", "nodes=1",
                      "nodeDv=42.500", "thr=0.250", "warp=NONEx1.000",
                      "sit=ORBITING"):
            self.assertIn(token, line)
        self.assertNotIn("LOST", line)

    def test_compact_marks_vessel_lost(self):
        line = mlib.format_snapshot_compact(
            mlib.TelemetrySnapshot(ut=1.0, vessel_lost=True))
        self.assertTrue(line.endswith(" LOST"))

    def test_snapshot_dict_json_safe(self):
        d = mlib.snapshot_dict(mlib.TelemetrySnapshot(ut=5.0))
        self.assertIsNone(d["nodeDv"])     # NaN default -> None
        self.assertIsNone(d["apError"])
        self.assertNotIn("NaN", json.dumps(d))
        self.assertEqual(d["ut"], 5.0)
        self.assertFalse(d["vesselLost"])

    def test_prox_ops_fields_fail_closed_defaults(self):
        # Flight-10 prox-ops fields default fail-closed: NaN angvel -> None in the
        # status dict, SAS/RCS off, empty AP status. A runner (or a B1-B7 snapshot
        # that never reads the docking surface) leaves them at these sentinels.
        s = mlib.TelemetrySnapshot(ut=1.0)
        self.assertFalse(math.isfinite(s.angular_velocity))
        self.assertFalse(s.sas_enabled)
        self.assertFalse(s.rcs_enabled)
        self.assertEqual(s.docking_ap_status, "")
        d = mlib.snapshot_dict(s)
        self.assertIsNone(d["angularVelocity"])   # NaN -> None
        self.assertFalse(d["sasEnabled"])
        self.assertFalse(d["rcsEnabled"])
        self.assertEqual(d["dockingApStatus"], "")
        self.assertNotIn("NaN", json.dumps(d))
        # The compact line carries the new tumble/control/AP-status tokens.
        line = mlib.format_snapshot_compact(s)
        for token in ("angV=", "sas=0", "rcs=0", "apSt="):
            self.assertIn(token, line)

    def test_node_executor_channel_is_opt_in_and_fails_closed(self):
        """The OBSERVED MechJeb NodeExecutor.Enabled channel (B11 flight 1).
        UNREAD is the -1 sentinel and emits NO compact token, so every mission
        that does not opt into the read keeps a byte-identical line."""
        s = mlib.TelemetrySnapshot(ut=1.0)
        self.assertEqual(s.node_executor_enabled, -1)
        self.assertEqual(mlib.snapshot_dict(s)["nodeExecutorEnabled"], -1)
        self.assertNotIn("nodeExec=", mlib.format_snapshot_compact(s))
        armed = mlib.TelemetrySnapshot(ut=1.0, node_executor_enabled=1)
        self.assertTrue(
            mlib.format_snapshot_compact(armed).endswith(" nodeExec=1"))
        down = mlib.TelemetrySnapshot(ut=1.0, node_executor_enabled=0)
        self.assertIn(" nodeExec=0", mlib.format_snapshot_compact(down))

    def test_warp_utilisation_row_names_a_thrashing_coast(self):
        """B12 flight 2 in one line: ~41,650 game seconds still to go after
        3,603 warp commands and a whole wall budget. The ratio IS the
        diagnosis."""
        row = mlib.warp_utilisation_row("COAST-TO-TARGET", wall_seconds=3900.0,
                                        game_seconds=5000.0, warp_commands=3603)
        self.assertAlmostEqual(row["gameSecondsPerWallSecond"], 1.282)
        self.assertEqual(row["warpCommands"], 3603)
        self.assertEqual(row["phase"], "COAST-TO-TARGET")
        # A healthy warped coast reads hundreds-to-thousands.
        healthy = mlib.warp_utilisation_row("COAST-TO-TARGET", 60.0, 60000.0, 1)
        self.assertAlmostEqual(healthy["gameSecondsPerWallSecond"], 1000.0)

    def test_warp_utilisation_row_survives_a_zero_or_unread_span(self):
        row = mlib.warp_utilisation_row("PRELAUNCH", 0.0, 0.0, 0)
        self.assertIsNone(row["gameSecondsPerWallSecond"])
        nan_row = mlib.warp_utilisation_row("PRELAUNCH", 5.0, float("nan"), 0)
        self.assertIsNone(nan_row["gameSecondsPerWallSecond"])
        self.assertIsNone(nan_row["gameSeconds"])
        self.assertNotIn("NaN", json.dumps(nan_row))

    def test_mission_result_omits_warp_utilisation_when_absent(self):
        """Byte-identical results for every path that never accumulated any."""
        base = dict(mission="b1", verdict=mlib.MISSION_OK, reason="ok",
                    phases_reached=["PRELAUNCH"], connect_attempts=1,
                    connected_seconds=1.0, rpc_port=50000, assertions=[],
                    wall_seconds=1.0, krpc_client_version="",
                    krpc_server_version="")
        self.assertNotIn("warpUtilisation", mlib.build_mission_result(**base))
        with_rows = mlib.build_mission_result(
            warp_utilisation=[mlib.warp_utilisation_row("X", 1.0, 10.0, 2)],
            **base)
        self.assertEqual(with_rows["warpUtilisation"][0]["warpCommands"], 2)

    def test_correction_giveup_rides_the_machine_line(self):
        """B12 flight 1: a correction round exit was indistinguishable from a
        clean cut in the log. The reason is now a diffed machine field."""
        line = mlib.format_machine_state(
            _b5_state(corr_giveup=mlib.CORR_GIVEUP_NO_PROGRESS,
                      corr_budget_anchor_ut=74193.5), ut=74200.0)
        self.assertIn("corrGiveup=no-progress", line)
        self.assertIn("corrBudgetAnchorUt=74193.500", line)
        self.assertIn(("corr_giveup", "corrGiveup"), mlib.MACHINE_DIFF_FIELDS)
        # A fresh machine carries neither (the "" / None sentinels).
        clean = mlib.format_machine_state(_b5_state(), ut=1.0)
        self.assertIn("corrGiveup=", clean)
        self.assertIn("corrBudgetAnchorUt=none", clean)

    def test_capture_executor_supervision_rides_the_machine_line(self):
        """The two hard-capped recovery counters + the observed-down debounce
        run must be greppable from the machine-state line."""
        line = mlib.format_machine_state(
            _b5_state(capture_exec_disabled_streak=2, capture_exec_reissues=1,
                      capture_replans_done=1), ut=100.0)
        for token in ("captureExecDownStreak=2", "captureExecReissues=1",
                      "captureReplans=1"):
            self.assertIn(token, line)


class StatusPathTests(unittest.TestCase):
    def test_mission_result_maps_to_status_sibling(self):
        self.assertEqual(
            mission_runner.status_path_for(
                os.path.join("results", "2026-07-22_1210_B5_mission.json")),
            os.path.join("results", "2026-07-22_1210_B5_status.json"))

    def test_foreign_result_name_gets_suffix(self):
        self.assertEqual(mission_runner.status_path_for("odd/result.json"),
                         "odd/result.json.status.json")


class FakeClock:
    def __init__(self, step=0.0):
        self.t = 0.0
        self.step = step

    def __call__(self):
        self.t += self.step
        return self.t


class StatusFileWriterTests(unittest.TestCase):
    def test_cadence_and_atomic_write(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "run_status.json")
            clock = FakeClock()
            w = mission_runner.StatusFileWriter(path, clock=clock, interval=2.0)
            builds = []

            def builder():
                builds.append(1)
                return {"phase": "COAST", "n": len(builds)}

            self.assertTrue(w.maybe_write(builder))        # first: due
            clock.t = 1.0
            self.assertFalse(w.maybe_write(builder))       # off-cadence:
            self.assertEqual(len(builds), 1)               # builder skipped
            clock.t = 3.0
            self.assertTrue(w.maybe_write(builder))
            with open(path, "r", encoding="ascii") as fh:
                data = json.load(fh)
            self.assertEqual(data["n"], 2)
            self.assertFalse(os.path.exists(path + ".tmp"))  # no tmp litter
            self.assertEqual(w.writes, 2)
            self.assertEqual(w.failures, 0)

    def test_failures_swallowed(self):
        w = mission_runner.StatusFileWriter(
            os.path.join("no-such-dir-xyz", "sub", "s.json"),
            clock=FakeClock(), interval=0.0)
        self.assertFalse(w.maybe_write(lambda: {"a": 1}))   # OSError swallowed
        self.assertEqual(w.failures, 1)
        self.assertFalse(w.maybe_write(lambda: (_ for _ in ()).throw(
            RuntimeError("builder blew up"))))              # builder swallowed
        self.assertEqual(w.failures, 2)


def _b1_happy_frames():
    s = mlib.TelemetrySnapshot
    return [
        s(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
        s(ut=1.0, stage_solid_fuel=0.5, apoapsis=14000, situation="FLYING"),
        s(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
        s(ut=3.0, vertical_speed=5.0, apoapsis=14000, situation="FLYING"),
        s(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
        s(ut=5.0, altitude=5000, apoapsis=14000, situation="FLYING"),
        s(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING"),
        s(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED"),
    ]


class FakeControl(mission_runner.MissionControl):
    def __init__(self, snaps):
        self._snaps = list(snaps)
        self._i = 0
        self.client_version = "0.5.4"
        self.server_version = "0.5.4"

    def open(self, host, rpc_port, stream_port):
        pass

    def read_snapshot(self):
        if self._i < len(self._snaps):
            snap = self._snaps[self._i]
            self._i += 1
            return snap
        return self._snaps[-1]

    def perform(self, action):
        pass

    def close(self):
        pass


class FakeFlightInstrumentationTests(unittest.TestCase):
    """End-to-end over the fake seam: one B1 flight produces the machine-state
    line, the ut= telemetry token, the window dumps, and a parseable status
    file with the machine + snapshot + events blocks."""

    def _fly(self, tmp):
        lines = []
        clock = FakeClock(step=0.001)
        log = mission_runner.MissionLogger(sink=lines.append, clock=clock)
        status_path = os.path.join(tmp, "x_status.json")
        writer_calls = {}
        status_writer = mission_runner.StatusFileWriter(
            status_path, clock=clock, interval=0.0,
            base={"mission": "b1_pad_hop", "rpcPort": 50000})
        result_sink = writer_calls.setdefault("result", [])
        code = mission_runner.run_mission(
            b1_pad_hop.SPEC,
            {"throttle": 1.0,
             "apoapsisWindowMeters": {"min": 6000, "max": 30000},
             "chuteDeployAltMeters": 2500,
             "landedSituations": ["LANDED"],
             "ascentTimeoutSeconds": 90, "coastTimeoutSeconds": 180,
             "descentTimeoutSeconds": 240},
            "127.0.0.1", 50000, 50001, os.path.join(tmp, "x_mission.json"),
            600.0, control=FakeControl(_b1_happy_frames()), log=log,
            clock=clock, sleep=lambda _s: None,
            writer=lambda p, t: result_sink.append((p, t)),
            status_writer=status_writer)
        return code, lines, status_path

    def test_flight_emits_observability_surface(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, lines, status_path = self._fly(tmp)
            self.assertEqual(code, 0)
            # 2a: machine-state line present and phase-stamped.
            machine = [l for l in lines if "] machine phase=" in l]
            self.assertTrue(machine, lines[:20])
            self.assertIn("[Mission][VerboseRateLimited]", machine[0])
            # 2a: telemetry line carries the trailing ut= token.
            telem = [l for l in lines if "] telemetry ap=" in l]
            self.assertTrue(telem)
            self.assertRegex(telem[0], r" ut=[-0-9.]+$")
            # 2c: window dump fired on the phase transitions, oldest first.
            dumps = [l for l in lines if "window dump reason=" in l]
            self.assertTrue(any("phase-transition" in l for l in dumps))
            frames = [l for l in lines if "window[01/" in l]
            self.assertTrue(frames)
            # 2d: the status file exists, parses, and carries the blocks.
            with open(status_path, "r", encoding="ascii") as fh:
                status = json.load(fh)
            self.assertEqual(status["mission"], "b1_pad_hop")
            self.assertEqual(status["schema"], 1)
            self.assertIn("machine", status)
            self.assertIn("phase", status["machine"])
            self.assertIn("snapshot", status)
            self.assertIn("events", status)
            # The tee captured sparse Info lines, not telemetry samples.
            self.assertTrue(all("] telemetry " not in e
                                for e in status["events"]))
            # G1: the WALL block rides every status write. The fake flight is
            # spawned with budget 600 and a FakeClock, so the numbers are exact
            # in shape (present, finite, budget verbatim) even though the fake
            # clock makes elapsed tiny.
            self.assertEqual(status["wallBudgetSeconds"], 600.0)
            self.assertIsNotNone(status["wallElapsedSeconds"])
            self.assertIsNotNone(status["wallRemainingSeconds"])
            self.assertAlmostEqual(
                status["wallElapsedSeconds"] + status["wallRemainingSeconds"],
                600.0, places=3)
            self.assertIsNotNone(status["phaseWallSeconds"])
            # G2: the OPEN phase's live warp row.
            self.assertIn("phaseWarp", status)
            self.assertIn("gameSecondsPerWallSecond", status["phaseWarp"])
            self.assertEqual(status["phaseWarp"]["phase"], status["phase"])
            # G4: exactly ONE gate-flip suppression summary per flight, and no
            # phase-transition dump was suppressed.
            summaries = [l for l in lines
                         if "gate-flip window dumps emitted=" in l]
            self.assertEqual(len(summaries), 1, summaries)
            self.assertIn("[Window]", summaries[0])

    def test_status_file_skipped_for_hermetic_tests(self):
        """writer-injected runs (the existing test_shells pattern) create NO
        status file unless one is passed explicitly."""
        lines = []
        clock = FakeClock(step=0.001)
        log = mission_runner.MissionLogger(sink=lines.append, clock=clock)
        captured = []
        code = mission_runner.run_mission(
            b1_pad_hop.SPEC,
            {"throttle": 1.0,
             "apoapsisWindowMeters": {"min": 6000, "max": 30000},
             "chuteDeployAltMeters": 2500,
             "landedSituations": ["LANDED"],
             "ascentTimeoutSeconds": 90, "coastTimeoutSeconds": 180,
             "descentTimeoutSeconds": 240},
            "127.0.0.1", 50000, 50001, "unused/x_mission.json", 600.0,
            control=FakeControl(_b1_happy_frames()), log=log, clock=clock,
            sleep=lambda _s: None,
            writer=lambda p, t: captured.append((p, t)))
        self.assertEqual(code, 0)
        self.assertFalse(os.path.exists("unused/x_status.json"))


class GateFlipDumpRateLimitTests(unittest.TestCase):
    """G4: the gate-flip window dump is the measured log amplifier (7,218
    dumps / 144,561 payload lines on ONE B12 run, 79.5% of a 43 MB log).
    ``mlib.should_dump_gate_flip_window`` is the whole decision."""

    def test_first_dump_is_always_admitted(self):
        self.assertTrue(mlib.should_dump_gate_flip_window(0.0, None, 10.0))
        self.assertTrue(mlib.should_dump_gate_flip_window(12345.0, None, 10.0))

    def test_dump_inside_the_interval_is_suppressed(self):
        self.assertFalse(mlib.should_dump_gate_flip_window(105.0, 100.0, 10.0))
        self.assertFalse(mlib.should_dump_gate_flip_window(100.5, 100.0, 10.0))

    def test_boundary_admits(self):
        # >= interval, so the boundary itself is a dump (contiguous, not
        # overlapping, ring coverage).
        self.assertTrue(mlib.should_dump_gate_flip_window(110.0, 100.0, 10.0))
        self.assertTrue(mlib.should_dump_gate_flip_window(110.1, 100.0, 10.0))

    def test_non_finite_inputs_fail_open(self):
        """Observability must never suppress on a clock fault: a NaN admits."""
        nan = float("nan")
        self.assertTrue(mlib.should_dump_gate_flip_window(nan, 100.0, 10.0))
        self.assertTrue(mlib.should_dump_gate_flip_window(110.0, nan, 10.0))
        self.assertTrue(mlib.should_dump_gate_flip_window(110.0, 100.0, nan))

    def test_limiter_counts_both_sides_and_summarizes(self):
        limiter = mission_runner._GateFlipDumpLimiter(interval=10.0)
        self.assertTrue(limiter.admit(0.0))       # first
        self.assertFalse(limiter.admit(1.0))      # inside
        self.assertFalse(limiter.admit(9.9))      # inside
        self.assertTrue(limiter.admit(10.0))      # boundary
        self.assertEqual((limiter.emitted, limiter.suppressed), (2, 2))
        msg = limiter.summary_message()
        self.assertIn("emitted=2", msg)
        self.assertIn("suppressed=2", msg)

    def test_interval_matches_the_ring_span(self):
        """The interval IS the ring's own span, so consecutive admitted dumps
        carry contiguous non-overlapping history."""
        self.assertEqual(
            mission_runner.GATE_FLIP_WINDOW_DUMP_INTERVAL_SECONDS,
            mission_runner.RING_BUFFER_FRAMES
            * mission_runner.POLL_INTERVAL_SECONDS)


class WallBudgetBlockTests(unittest.TestCase):
    """G1: the WALL accounting the live surface never had. A B12 run died on
    mission-budget-expired after burning 57% of its wall budget in one phase
    while every displayed (GAME) budget read ~7.5% consumed."""

    def test_elapsed_remaining_and_budget(self):
        block = mlib.wall_budget_block(now=100.0, deadline=4300.0,
                                       wall_budget=4200.0)
        self.assertEqual(block["wallBudgetSeconds"], 4200.0)
        self.assertEqual(block["wallRemainingSeconds"], 4200.0)
        self.assertEqual(block["wallElapsedSeconds"], 0.0)

    def test_mid_run_split_sums_to_the_budget(self):
        # The measured incident: 2,394 s of a 4,200 s wall budget consumed.
        block = mlib.wall_budget_block(now=2394.0, deadline=4200.0,
                                       wall_budget=4200.0)
        self.assertEqual(block["wallElapsedSeconds"], 2394.0)
        self.assertEqual(block["wallRemainingSeconds"], 1806.0)
        self.assertEqual(block["wallElapsedSeconds"]
                         + block["wallRemainingSeconds"], 4200.0)

    def test_missing_budget_still_yields_remaining(self):
        block = mlib.wall_budget_block(now=10.0, deadline=100.0,
                                       wall_budget=None)
        self.assertIsNone(block["wallBudgetSeconds"])
        self.assertIsNone(block["wallElapsedSeconds"])
        self.assertEqual(block["wallRemainingSeconds"], 90.0)

    def test_no_deadline_yields_all_none_but_keeps_the_budget(self):
        block = mlib.wall_budget_block(now=float("nan"), deadline=None,
                                       wall_budget=4200.0)
        self.assertEqual(block["wallBudgetSeconds"], 4200.0)
        self.assertIsNone(block["wallElapsedSeconds"])
        self.assertIsNone(block["wallRemainingSeconds"])


if __name__ == "__main__":
    unittest.main()
