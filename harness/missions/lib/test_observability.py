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
        "chuteArmMaxRateMps": 30, "chuteFullDeployAltMeters": 1000,
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
        # The chute arms at the apoapsis crossing above (|vs| 5 <= 30) and the canopy
        # READS open on the way down: craftCanopyObserved is an assertion now, so a
        # happy-path fake flight has to model a canopy that actually opened.
        s(ut=5.0, altitude=5000, apoapsis=14000, situation="FLYING",
          craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),
        s(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING",
          craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        s(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED",
          craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
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
             "chuteArmMaxRateMps": 30, "chuteFullDeployAltMeters": 1000,
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
             "chuteArmMaxRateMps": 30, "chuteFullDeployAltMeters": 1000,
             "landedSituations": ["LANDED"],
             "ascentTimeoutSeconds": 90, "coastTimeoutSeconds": 180,
             "descentTimeoutSeconds": 240},
            "127.0.0.1", 50000, 50001, "unused/x_mission.json", 600.0,
            control=FakeControl(_b1_happy_frames()), log=log, clock=clock,
            sleep=lambda _s: None,
            writer=lambda p, t: captured.append((p, t)))
        self.assertEqual(code, 0)
        self.assertFalse(os.path.exists("unused/x_status.json"))


if __name__ == "__main__":
    unittest.main()
