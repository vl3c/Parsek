"""MechJeb ascent engage forces the shared node-executor autowarp flag on.

``Core.Node.Autowarp`` is global MechJeb state saved across lanes; the
AscentAutopilot gates its own physics warp and its circularization-coast warp
on it. An ascent engaged with the flag left False by an earlier lane coasts to
apoapsis at 1x (2026-11-05 full run: B7's retry and B16, ~20 wall-min each).
"""

import os
import sys
import types
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
for _p in (_HERE, _MISSIONS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import mission_runner  # noqa: E402
import mlib  # noqa: E402
from mlib import Action  # noqa: E402


class _Ascent:
    def __init__(self, log):
        self._log = log
        self._enabled = False

    @property
    def enabled(self):
        return self._enabled

    @enabled.setter
    def enabled(self, value):
        self._log.append(("ascent.enabled", bool(value)))
        self._enabled = bool(value)


class _NodeExecutor:
    def __init__(self, log, autowarp=False):
        self._log = log
        self._autowarp = autowarp
        self.executed = 0

    @property
    def autowarp(self):
        return self._autowarp

    @autowarp.setter
    def autowarp(self, value):
        self._log.append(("ne.autowarp", bool(value)))
        self._autowarp = bool(value)

    def execute_all_nodes(self):
        self._log.append(("ne.execute_all_nodes",))
        self.executed += 1


class _BrokenNodeExecutor:
    @property
    def autowarp(self):
        raise RuntimeError("read refused")

    @autowarp.setter
    def autowarp(self, value):
        raise RuntimeError("write refused")


def _control(node_executor, ascent, nodes=()):
    c = mission_runner.KrpcMissionControl(use_mechjeb=True)
    c._mechjeb = types.SimpleNamespace(node_executor=node_executor)
    c._ascent = ascent
    vessel = types.SimpleNamespace(control=types.SimpleNamespace(nodes=nodes))
    c._conn = types.SimpleNamespace(
        space_center=types.SimpleNamespace(active_vessel=vessel))
    return c


class AscentAutowarpTests(unittest.TestCase):

    def setUp(self):
        self.lines = []
        self._orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = self.lines.append

    def tearDown(self):
        mission_runner._stdout_sink = self._orig

    def test_engage_sets_autowarp_before_enabling_the_ascent(self):
        log = []
        ne = _NodeExecutor(log, autowarp=False)
        c = _control(ne, _Ascent(log))
        c.perform(Action(mlib.ACTION_MJ_ENGAGE_ASCENT))
        self.assertIs(True, ne.autowarp)
        self.assertEqual([("ne.autowarp", True), ("ascent.enabled", True)], log)
        self.assertTrue(any("[MjConfig]" in l and "forced on for ascent engage"
                            in l and "(was False)" in l for l in self.lines),
                        self.lines)

    def test_engage_logs_an_already_set_flag(self):
        log = []
        ne = _NodeExecutor(log, autowarp=True)
        c = _control(ne, _Ascent(log))
        c.perform(Action(mlib.ACTION_MJ_ENGAGE_ASCENT))
        self.assertIs(True, ne.autowarp)
        self.assertTrue(any("(was True)" in l for l in self.lines), self.lines)

    def test_refused_engage_touches_nothing(self):
        log = []
        ne = _NodeExecutor(log, autowarp=False)
        c = _control(ne, _Ascent(log))
        c._mj_config_refusal = "autostage limit refused"
        c.perform(Action(mlib.ACTION_MJ_ENGAGE_ASCENT))
        self.assertEqual([], log)
        self.assertIs(False, ne.autowarp)

    def test_a_failing_flag_still_engages_the_ascent(self):
        log = []
        ascent = _Ascent(log)
        c = _control(_BrokenNodeExecutor(), ascent)
        c.perform(Action(mlib.ACTION_MJ_ENGAGE_ASCENT))
        self.assertTrue(ascent.enabled)
        self.assertTrue(any("[Warn]" in l and "autowarp=True failed for ascent engage"
                            in l for l in self.lines), self.lines)

    def test_circularization_sets_autowarp_before_executing(self):
        log = []
        ne = _NodeExecutor(log, autowarp=False)
        c = _control(ne, _Ascent(log), nodes=(object(),))
        c.perform(Action(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION))
        self.assertEqual([("ne.autowarp", True), ("ne.execute_all_nodes",)], log)

    def test_circularization_with_no_node_touches_nothing(self):
        log = []
        ne = _NodeExecutor(log, autowarp=False)
        c = _control(ne, _Ascent(log), nodes=())
        c.perform(Action(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION))
        self.assertEqual([], log)


if __name__ == "__main__":
    unittest.main()
