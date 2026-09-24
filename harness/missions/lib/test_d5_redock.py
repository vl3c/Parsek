"""Unit cells for the D5 same-tree re-dock (mission `d5_redock`, scenario
`SD-1-same-tree-redock`, fixture `bdock-second-dock-recorded`).

Three groups:

  1. THE PURE MACHINE (`mlib.rdock_decide`): the undock / back-off / target / dock /
     settle order, the fail-closed start gate (a booted vessel that is not a docked
     pair flakes instead of flying), the DOCK hand-off to `bdock_decide` and its
     corroborated completion, and a named give-up in each own phase.
  2. THE SHELL, driven end to end over a scripted flight with no krpc and no KSP.
  3. SPEC / SCHEMA SYNC, and the fixture shape the lane depends on: the booted active
     vessel is a docked pair carrying two docking ports.

NO krpc, NO KSP, NO network.
"""

import os
import re
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mlib                        # noqa: E402
import d5_redock                   # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "SD-1-same-tree-redock.toml")
SCHEMA_PATH = os.path.join(_MISSIONS, "d5_redock.schema.toml")
FIXTURE_SFS = os.path.join(_HARNESS, "fixtures", "saves", "bdock-second-dock-recorded",
                           "persistent.sfs")

PARAMS = mlib.RDockParams(start_settle_seconds=0.0, backoff_seconds=20.0,
                          settle_seconds=10.0)

HAPPY = [
    snap(ut=1000.0, docking_state="Docked", vessel_count=12),          # -> RD-UNDOCK
    snap(ut=1001.0, docking_state="Docked", vessel_count=12),
    snap(ut=1002.0, docking_state="Ready", vessel_count=13),           # -> RD-BACKOFF
    snap(ut=1010.0, docking_state="Ready", vessel_count=13),
    snap(ut=1023.0, docking_state="Ready", vessel_count=13),           # -> RD-TARGET
    snap(ut=1024.0, docking_state="Ready", vessel_count=13,
         target_set=True, target_distance=6.0),                        # -> DOCK
    snap(ut=1025.0, docking_state="Ready", vessel_count=13, target_set=True,
         target_distance=6.0),                                         # deferred enable
    snap(ut=1030.0, docking_state="Ready", vessel_count=13, target_set=True,
         target_distance=20.0, mj_docking_enabled=True),
    snap(ut=1090.0, docking_state="Docking", vessel_count=13, target_set=True,
         target_distance=0.5, mj_docking_enabled=True),
    snap(ut=1095.0, docking_state="Docked", vessel_count=12, target_set=True,
         target_distance=0.3, mj_docking_enabled=False),              # -> RD-SETTLE
    snap(ut=1100.0, docking_state="Docked", vessel_count=12),
    snap(ut=1106.0, docking_state="Docked", vessel_count=12),         # -> RD-TERMINAL
]


def _walk(frames, params=PARAMS):
    st = mlib.rdock_initial_state(params)
    log = []
    for f in frames:
        st, actions = mlib.rdock_decide(st, f)
        log.append((st.phase, [a.kind for a in actions]))
        if st.done:
            break
    return st, log


class RDockMachineTests(unittest.TestCase):

    def test_happy_path_reaches_terminal_in_order_with_the_expected_actions(self):
        st, log = _walk(HAPPY)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(st.phases_reached, (
            mlib.RDOCK_START, mlib.RDOCK_UNDOCK, mlib.RDOCK_BACKOFF, mlib.RDOCK_TARGET,
            mlib.BDOCK_DOCK, mlib.RDOCK_SETTLE, mlib.RDOCK_TERMINAL))
        self.assertEqual(log[0], (mlib.RDOCK_UNDOCK, [
            mlib.ACTION_SET_SAS, mlib.ACTION_SET_RCS, mlib.ACTION_UNDOCK]))
        self.assertIn((mlib.RDOCK_TARGET, [mlib.ACTION_TARGET_NEAREST_NAMED_VESSEL]), log)
        self.assertIn((mlib.BDOCK_DOCK, [mlib.ACTION_SET_SAS, mlib.ACTION_SET_RCS,
                                         mlib.ACTION_SET_TARGET_DOCKING_PORT]), log)
        # The docking AP is enabled one poll AFTER the port target (flight 9).
        self.assertIn((mlib.BDOCK_DOCK, [mlib.ACTION_MJ_ENABLE_DOCKING]), log)
        # B-DOCK's TRANSFER entry is replaced by the settle: no transfer is started.
        kinds = [k for _p, ks in log for k in ks]
        self.assertNotIn(mlib.ACTION_START_RESOURCE_TRANSFER, kinds)
        self.assertIn((mlib.RDOCK_SETTLE, [mlib.ACTION_MJ_DISABLE_DOCKING]), log)
        outcomes = mlib.evaluate_rdock_assertions([], PARAMS, st.phases_reached, st)
        self.assertTrue(all(o.met for o in outcomes), outcomes)

    def test_target_action_names_the_craft(self):
        st = mlib.rdock_initial_state(mlib.RDockParams(craft_name="Kerbal X",
                                                       start_settle_seconds=0.0,
                                                       backoff_seconds=0.0))
        st, _ = mlib.rdock_decide(st, snap(ut=1.0, docking_state="Docked", vessel_count=3))
        st, _ = mlib.rdock_decide(st, snap(ut=2.0, docking_state="Ready", vessel_count=4))
        st, actions = mlib.rdock_decide(st, snap(ut=3.0, docking_state="Ready",
                                                 vessel_count=4))
        self.assertEqual(st.phase, mlib.RDOCK_TARGET)
        self.assertEqual([(a.kind, a.text) for a in actions],
                         [(mlib.ACTION_TARGET_NEAREST_NAMED_VESSEL, "Kerbal X")])

    def test_the_undock_waits_for_the_start_dwell(self):
        # The scene-entry restore re-binds the pair's recording a second or two
        # after the load; an undock before it would split an unrecorded vessel.
        st = mlib.rdock_initial_state(mlib.RDockParams(start_settle_seconds=10.0))
        for ut in (500.0, 509.9):
            st, actions = mlib.rdock_decide(st, snap(ut=ut, docking_state="Docked",
                                                     vessel_count=12))
            self.assertEqual((st.phase, actions), (mlib.RDOCK_START, []))
        st, actions = mlib.rdock_decide(st, snap(ut=510.0, docking_state="Docked",
                                                 vessel_count=12))
        self.assertEqual(st.phase, mlib.RDOCK_UNDOCK)
        self.assertEqual(st.undock_baseline_vessel_count, 12)
        self.assertIn(mlib.ACTION_UNDOCK, [a.kind for a in actions])

    def test_backoff_waits_its_full_dwell(self):
        st, _ = _walk(HAPPY[:3])
        self.assertEqual(st.phase, mlib.RDOCK_BACKOFF)
        st, actions = mlib.rdock_decide(st, snap(ut=1021.9, docking_state="Ready",
                                                 vessel_count=13))
        self.assertEqual((st.phase, actions), (mlib.RDOCK_BACKOFF, []))

    def test_a_booted_vessel_that_is_not_docked_never_undocks_and_flakes_named(self):
        st = mlib.rdock_initial_state(PARAMS)
        for ut in (0.0, 30.0):
            st, actions = mlib.rdock_decide(st, snap(ut=ut, docking_state="Ready",
                                                     vessel_count=12))
            self.assertEqual(actions, [])
        st, actions = mlib.rdock_decide(st, snap(ut=61.0, docking_state="Ready",
                                                 vessel_count=12))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("never read a Docked port", st.flake_reason)
        self.assertEqual(actions, [])

    def test_an_unread_vessel_count_does_not_start_the_undock(self):
        # vessel_count 0 is the UNREAD sentinel: a baseline of 0 would let any read
        # certify the split, so the start gate waits for a real count.
        st = mlib.rdock_initial_state(PARAMS)
        st, actions = mlib.rdock_decide(st, snap(ut=0.0, docking_state="Docked",
                                                 vessel_count=0))
        self.assertEqual((st.phase, actions), (mlib.RDOCK_START, []))

    def test_undock_needs_both_the_count_rise_and_a_not_docked_port(self):
        st, _ = _walk(HAPPY[:1])
        st, _ = mlib.rdock_decide(st, snap(ut=1001.0, docking_state="Docked",
                                           vessel_count=13))
        self.assertEqual(st.phase, mlib.RDOCK_UNDOCK)
        st, _ = mlib.rdock_decide(st, snap(ut=1002.0, docking_state="Ready",
                                           vessel_count=12))
        self.assertEqual(st.phase, mlib.RDOCK_UNDOCK)
        st, _ = mlib.rdock_decide(st, snap(ut=1200.0, docking_state="Docked",
                                           vessel_count=12))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no undock split observed", st.flake_reason)

    def test_target_give_up_is_named(self):
        st, _ = _walk(HAPPY[:5])
        self.assertEqual(st.phase, mlib.RDOCK_TARGET)
        st, _ = mlib.rdock_decide(st, snap(ut=1090.0, docking_state="Ready",
                                           vessel_count=13))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no target acquired", st.flake_reason)

    def test_a_dock_phase_flake_surfaces_bdocks_reason(self):
        st, _ = _walk(HAPPY[:6])
        self.assertEqual(st.phase, mlib.BDOCK_DOCK)
        ut = 1025.0
        while not st.done and ut < 4000.0:
            # Altitude moves so the frozen-telemetry detector stays quiet; distance,
            # monoprop and spin stay flat, which is B-DOCK's no-progress watchdog.
            st, _ = mlib.rdock_decide(st, snap(ut=ut, altitude=100000.0 + ut,
                                               docking_state="Ready",
                                               vessel_count=13, target_set=True,
                                               target_distance=20.0,
                                               mj_docking_enabled=True))
            ut += 5.0
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(st.flake_phase, mlib.BDOCK_DOCK)
        self.assertFalse(st.redock_confirmed)

    def test_assertions_fail_without_the_redock(self):
        st, _ = _walk(HAPPY[:6])
        outcomes = {o.name: o.met for o in mlib.evaluate_rdock_assertions(
            [], PARAMS, st.phases_reached, st)}
        self.assertEqual(outcomes, {"startedDocked": True, "undocked": True,
                                    "redocked": False})

    def test_params_from_dict(self):
        p = mlib.rdock_params_from_dict({
            "craftName": "X", "startSettleSeconds": 7, "startTimeoutSeconds": 1,
            "undockTimeoutSeconds": 2,
            "backoffSeconds": 3, "targetTimeoutSeconds": 4, "settleSeconds": 5,
            "dockSpeedMetersPerSec": 0.4, "dockTimeoutSeconds": 900})
        self.assertEqual((p.craft_name, p.start_timeout, p.undock_timeout,
                          p.backoff_seconds, p.target_timeout, p.settle_seconds),
                         ("X", 1.0, 2.0, 3.0, 4.0, 5.0))
        self.assertEqual((p.bdock.dock_speed, p.bdock.dock_timeout), (0.4, 900.0))
        self.assertEqual(p.start_settle_seconds, 7.0)


class RDockShellTests(unittest.TestCase):

    def test_shell_flies_the_scripted_mission_to_mission_ok(self):
        control = FakeMissionControl(HAPPY)
        params = {"craftName": "Kerbal X", "startSettleSeconds": 0,
                  "undockTimeoutSeconds": 120,
                  "backoffSeconds": 20, "dockSpeedMetersPerSec": 0.5,
                  "dockTimeoutSeconds": 1800}
        code, result = run(d5_redock.SPEC, params, control, budget=9000.0)
        self.assertEqual(result["mission"], "d5_redock")
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)


class RDockSpecSyncTests(unittest.TestCase):

    def _spec(self):
        with open(SPEC_PATH, "rb") as fh:
            return tomllib.load(fh)

    def test_spec_names_this_mission_and_satisfies_the_schema(self):
        spec = self._spec()
        self.assertEqual(spec["driver"]["mission"], "d5_redock")
        with open(SCHEMA_PATH, "rb") as fh:
            schema = tomllib.load(fh)["params"]
        params = spec["driver"]["missionParams"]
        for key, rule in schema.items():
            if rule.get("required"):
                self.assertIn(key, params, key)
        for key in params:
            self.assertIn(key, schema, "undeclared mission param %s" % key)

    def test_the_booted_active_vessel_is_a_docked_pair_with_two_ports(self):
        spec = self._spec()
        self.assertEqual(spec["fixture"]["saveTemplate"],
                         "fixtures/saves/bdock-second-dock-recorded")
        with open(FIXTURE_SFS, "r", encoding="utf-8") as fh:
            text = fh.read().replace("\r\n", "\n")
        flight = text[text.index("\tFLIGHTSTATE"):]
        active = int(re.search(r"\n\t\tactiveVessel = (\d+)\n", flight).group(1))
        vessels = re.split(r"\n\t\tVESSEL\n", flight)[1:]
        vessel = vessels[active]
        self.assertIn("\n\t\t\tname = Kerbal X\n", vessel)
        states = re.findall(r"\n\t+name = ModuleDockingNode\n(?:.*\n)*?\t+state = (.*)\n",
                            vessel)
        self.assertEqual(sorted(s.strip() for s in states),
                         ["Docked (dockee)", "Docked (docker)"])


if __name__ == "__main__":
    unittest.main()
