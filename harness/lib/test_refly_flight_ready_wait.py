"""Guard for REFLY-LANES-JUMP-BEFORE-FLIGHT-READY (the CI-2 race).

`InvokeRewind` answers OK at `Invocation complete`, before the re-fly scene's
`OnFlightReady` builds the ghost chains and binds the re-fly recorder. A `TimeJump` or
`WarpToUT` sent in that window can cross a chain tip with no chain built, or fly the
attempt with no recorder. Every committed spec therefore puts a readiness read between
an accepted `InvokeRewind` and the next time step of the same scene: `ListHandles
kind=chains` (deferred until `OnFlightReady` ran) or `RecordingState
awaitRecorderLive=refly` (deferred until a recorder is live under the re-fly marker).

A step that leaves the re-fly scene (a reload, the merge dialog, the quit, another
rewind) ends the window: a time step after it is not racing this rewind. A rewind the
spec expects REJECTED changes no scene, so it opens no window (S4.1).

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib
"""

import os
import tomllib
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCENARIOS_DIR = os.path.join(os.path.dirname(HERE), "scenarios")

TIME_STEP_CMDS = frozenset({"TimeJump", "WarpToUT"})
SCENE_ENDING_CMDS = frozenset({
    "LoadGame", "AnswerMergeDialog", "FlushAndQuit", "InvokeRewind", "ReFlyRevert",
})


def is_flight_ready_wait(step):
    """True for the two seam reads that defer until the re-fly scene is ready."""
    args = step.get("args") or {}
    cmd = step.get("cmd")
    if cmd == "ListHandles" and args.get("kind") == "chains":
        return True
    return cmd == "RecordingState" and args.get("awaitRecorderLive") == "refly"


def unguarded_time_steps(steps):
    """(rewind_index, time_step_index) for every time step that follows an accepted
    InvokeRewind in the same scene with no flight-ready wait between them."""
    found = []
    for i, step in enumerate(steps):
        if step.get("cmd") != "InvokeRewind" or step.get("expect") != "OK":
            continue
        waited = False
        for j in range(i + 1, len(steps)):
            later = steps[j]
            cmd = later.get("cmd")
            if is_flight_ready_wait(later):
                waited = True
            if cmd in TIME_STEP_CMDS:
                if not waited:
                    found.append((i, j))
                break
            if cmd in SCENE_ENDING_CMDS:
                break
    return found


class UnguardedTimeStepRuleTests(unittest.TestCase):
    REWIND = {"cmd": "InvokeRewind", "args": {"rp": "x", "slot": "1"}, "expect": "OK"}
    JUMP = {"cmd": "TimeJump", "args": {"ut": "500"}, "expect": "OK"}

    def test_a_jump_straight_after_the_rewind_is_flagged(self):
        self.assertEqual(unguarded_time_steps([self.REWIND, self.JUMP]), [(0, 1)])

    def test_a_warp_straight_after_the_rewind_is_flagged(self):
        warp = {"cmd": "WarpToUT", "args": {"ut": "600"}, "expect": "OK"}
        self.assertEqual(unguarded_time_steps([self.REWIND, warp]), [(0, 1)])

    def test_either_wait_guards_the_jump(self):
        for wait in ({"cmd": "ListHandles", "args": {"kind": "chains"}, "expect": "OK"},
                     {"cmd": "RecordingState", "args": {"awaitRecorderLive": "refly"},
                      "expect": "OK"}):
            with self.subTest(wait=wait["cmd"]):
                self.assertEqual(unguarded_time_steps([self.REWIND, wait, self.JUMP]), [])

    def test_other_reads_do_not_guard_the_jump(self):
        plain = [{"cmd": "RecordingState", "expect": "OK"},
                 {"cmd": "ListHandles", "args": {"kind": "committed"}, "expect": "OK"}]
        self.assertEqual(unguarded_time_steps([self.REWIND] + plain + [self.JUMP]),
                         [(0, 3)])

    def test_a_rejected_rewind_opens_no_window(self):
        rejected = dict(self.REWIND, expect="REJECTED")
        self.assertEqual(unguarded_time_steps([rejected, self.JUMP]), [])

    def test_a_scene_change_closes_the_window(self):
        reload = {"cmd": "LoadGame", "args": {"name": "persistent"}, "expect": "OK"}
        self.assertEqual(unguarded_time_steps([self.REWIND, reload, self.JUMP]), [])


class CommittedSpecsWaitForFlightReadyTests(unittest.TestCase):
    def test_no_committed_spec_jumps_before_the_refly_scene_is_ready(self):
        names = sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml"))
        self.assertTrue(names)
        offenders = []
        for name in names:
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            steps = (spec.get("driver") or {}).get("steps") or []
            for rewind, jump in unguarded_time_steps(steps):
                offenders.append("%s: step %d (%s) follows InvokeRewind step %d"
                                 % (name, jump, steps[jump].get("cmd"), rewind))
        self.assertEqual(offenders, [], "add `ListHandles kind=chains` and/or "
                         "`RecordingState awaitRecorderLive=refly` after the rewind")


if __name__ == "__main__":
    unittest.main()
