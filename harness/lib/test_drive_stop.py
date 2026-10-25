"""Unit tests for the step-unmet drive stop (todo
SEAM-DRIVER-KEEPS-DRIVING-AFTER-A-REFUSED-STEP): ``missionverify.drive_stop_subkind``
(when a refused seam step ends the drive) and ``hlib.plan_unmet_step_tail`` (what
still runs after it). The end-to-end cells over the fake KSP live in
``test_run_smoke.UnmetStepStopSmokeTests``.
"""

import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.append(_HERE)

import hlib  # noqa: E402
import missionverify  # noqa: E402


def _ev(rows, responses):
    """rows: [(index, cmd, expect)] for the DRIVEN seam steps, in order (a mission
    step's index is simply absent); responses: {index: (verdict, msg)}."""
    steps = [{"id": hlib.step_id_for_index(i), "cmd": cmd, "expect": expect}
             for i, cmd, expect in rows]
    cmds = {i: cmd for i, cmd, _ in rows}
    lines = []
    for i, (verdict, msg) in responses.items():
        line = "id=%s cmd=%s verdict=%s seq=%d" % (hlib.step_id_for_index(i), cmds[i],
                                                   verdict, i + 1)
        if msg:
            line += " msg=%s" % msg
        lines.append(line)
    return hlib.evaluate_response_stream(lines, steps)


def _mission(index, met=True):
    return {"id": hlib.step_id_for_index(index), "phase": "mission", "expect": "MISSION-OK",
            "met": met, "missionVerdict": "MISSION-OK" if met else "MISSION-ASSERT-FAIL",
            "subkind": "" if met else "mission"}


class DriveStopSubkindTests(unittest.TestCase):

    def test_seam_only_refusal_stops_with_its_own_subkind(self):
        # The IR-2 shape: LaunchFromEditor REJECTED launchfromeditor-site-obstructed
        # -> driver-gate (the subkind both IR-2 attempts carried), and the drive stops.
        ev = _ev([(0, "LoadGame", "OK"), (1, "LaunchFromEditor", "OK")],
                 {0: ("OK", ""), 1: ("REJECTED", "launchfromeditor-site-obstructed")})
        self.assertEqual("driver-gate", missionverify.drive_stop_subkind(ev, None))

    def test_seam_only_unknown_refusal_falls_back_to_the_coarse_subkind(self):
        ev = _ev([(0, "LoadGame", "OK"), (1, "ListHandles", "OK")],
                 {0: ("OK", ""), 1: ("REJECTED", "kind-arg-missing")})
        self.assertEqual("driver-verdict-mismatch", missionverify.drive_stop_subkind(ev, None))

    def test_seam_timeout_and_failed_load_stop_too(self):
        ev = _ev([(0, "LoadGame", "OK")], {0: ("ERROR", "")})
        self.assertEqual("load-failed", missionverify.drive_stop_subkind(ev, None))
        ev = _ev([(0, "LoadGame", "OK"), (1, "TimeJump", "OK")],
                 {0: ("OK", ""), 1: ("TIMEOUT", "")})
        self.assertEqual("seam-timeout", missionverify.drive_stop_subkind(ev, None))

    def test_all_met_prefix_does_not_stop(self):
        ev = _ev([(0, "LoadGame", "OK"), (1, "CommitTree", "OK")],
                 {0: ("OK", ""), 1: ("OK", "")})
        self.assertEqual("", missionverify.drive_stop_subkind(ev, None))

    def test_an_expected_rejection_is_met_and_does_not_stop(self):
        ev = _ev([(0, "LoadGame", "OK"), (1, "InvokeRewind", "REJECTED")],
                 {0: ("OK", ""), 1: ("REJECTED", "refly-gate")})
        self.assertEqual("", missionverify.drive_stop_subkind(ev, None))

    def test_post_mission_outcome_refusal_stops(self):
        # Mission at index 1, met; EvaExit (an outcome verb) REJECTED -> driver fault.
        ev = _ev([(0, "LoadGame", "OK"), (2, "EvaExit", "OK")],
                 {0: ("OK", ""), 2: ("REJECTED", "kerbal-not-aboard")})
        self.assertTrue(missionverify.drive_stop_subkind(ev, _mission(1)))

    def test_post_mission_flight_outcome_keeps_driving(self):
        # EvaChuteDeploy ERROR is a FLIGHT OUTCOME -> PARSEK-FAIL(mission-outcome); the
        # driver stage stays valid, so the tail the verifiers read is still driven.
        ev = _ev([(0, "LoadGame", "OK"), (2, "EvaChuteDeploy", "OK")],
                 {0: ("OK", ""), 2: ("ERROR", "eva-chute-kerbal-lost")})
        self.assertEqual("", missionverify.drive_stop_subkind(ev, _mission(1)))

    def test_post_mission_non_refusal_recording_miss_keeps_driving(self):
        # A recording verb's ERROR after a MET mission is non-gating (the run's own
        # verifiers judge what Parsek recorded), so it must not stop the drive.
        ev = _ev([(0, "LoadGame", "OK"), (2, "CommitTree", "OK")],
                 {0: ("OK", ""), 2: ("ERROR", "")})
        self.assertEqual("", missionverify.drive_stop_subkind(ev, _mission(1)))

    def test_post_mission_recording_timeout_stops_the_drive(self):
        # DRIVER-VALIDITY-PASSES-WITH-UNMET-STEPS: the seam never reached a terminal, so
        # the stage is a driver-INVALID and only the cleanup tail is worth driving.
        ev = _ev([(0, "LoadGame", "OK"), (2, "ExitToSpaceCenter", "OK")],
                 {0: ("OK", ""), 2: ("TIMEOUT", "not-in-flight")})
        self.assertEqual("seam-timeout", missionverify.drive_stop_subkind(ev, _mission(1)))

    def test_an_unmet_mission_leaves_the_tail_to_the_mission_policy(self):
        ev = _ev([(0, "LoadGame", "OK"), (2, "StopRecording", "OK")],
                 {0: ("OK", ""), 2: ("TIMEOUT", "")})
        self.assertEqual("", missionverify.drive_stop_subkind(ev, _mission(1, met=False)))


class PlanUnmetStepTailTests(unittest.TestCase):

    STEPS = [
        {"cmd": "LoadGame"}, {"cmd": "LaunchFromEditor"}, {"cmd": "MissionMark"},
        {"cmd": "TimeJump"}, {"cmd": "StopRecording"}, {"cmd": "RevertToLaunch"},
        {"cmd": "FlushAndQuit"},
    ]

    def test_only_cleanup_runs_after_the_unmet_step(self):
        plan = hlib.plan_unmet_step_tail(self.STEPS, 1)
        self.assertEqual((4, 6), plan.run_indices)
        self.assertEqual((2, 3, 5), plan.skipped_indices)
        self.assertIn("skipping 3 of 5 remaining step(s)", plan.summary)
        self.assertIn("0007:FlushAndQuit", plan.summary)

    def test_a_later_mission_step_is_skipped(self):
        steps = [{"cmd": "LoadGame"}, {"cmd": "SetSetting"}, {"phase": "mission"},
                 {"cmd": "CommitTree"}, {"cmd": "FlushAndQuit"}]
        plan = hlib.plan_unmet_step_tail(steps, 0)
        self.assertEqual((1, 2, 3), plan.skipped_indices)
        self.assertEqual((4,), plan.run_indices)

    def test_nothing_after_the_last_step(self):
        plan = hlib.plan_unmet_step_tail(self.STEPS, 6)
        self.assertEqual((), plan.skipped_indices)
        self.assertEqual("no steps after the unmet step", plan.summary)


if __name__ == "__main__":
    unittest.main()
