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
SEPARATED_SPEC_PATH = os.path.join(_HARNESS, "scenarios", "LAB-13b-lab-dock-undock.toml")
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

    def test_the_undock_is_confirmed_by_the_active_part_count(self):
        # The global vessel count can be cancelled by an unrelated removal; the
        # active vessel's own part count falling is the channel that cannot.
        st = mlib.rdock_initial_state(mlib.RDockParams(start_settle_seconds=0.0))
        st, _ = mlib.rdock_decide(st, snap(ut=500.0, docking_state="Docked",
                                           vessel_count=12, part_count=56))
        self.assertEqual(st.phase, mlib.RDOCK_UNDOCK)
        self.assertEqual(st.undock_baseline_part_count, 56)
        for parts, state in ((28, "Docked"), (56, "Ready"), (0, "Ready"),
                             (70, "Ready")):
            st, _ = mlib.rdock_decide(st, snap(ut=501.0, docking_state=state,
                                               vessel_count=12, part_count=parts))
            self.assertEqual(st.phase, mlib.RDOCK_UNDOCK, (parts, state))
        st, _ = mlib.rdock_decide(st, snap(ut=502.0, docking_state="Ready",
                                           vessel_count=11, part_count=28))
        self.assertEqual(st.phase, mlib.RDOCK_BACKOFF)
        self.assertTrue(st.undock_confirmed)

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


LAB = "LABX Orbital Lab"
SEP_PARAMS = mlib.RDockParams(craft_name=LAB, start_separated=True,
                              start_settle_seconds=5.0, backoff_seconds=10.0,
                              settle_seconds=10.0)

# The separated start: the lab (23 parts) active with its port Ready after the spec's
# own split, the switch landing on the tender (9 parts), then RD-BACKOFF / TARGET / DOCK.
SEP_HAPPY = [
    snap(ut=400.0, docking_state="Ready", vessel_count=2, part_count=23),
    snap(ut=405.0, docking_state="Ready", vessel_count=2, part_count=23),     # -> RD-SWITCH
    snap(ut=406.0, docking_state="Ready", vessel_count=2, part_count=23),
    snap(ut=407.0, docking_state="Ready", vessel_count=2, part_count=9),      # -> RD-BACKOFF
    snap(ut=417.0, docking_state="Ready", vessel_count=2, part_count=9),      # -> RD-TARGET
    snap(ut=418.0, docking_state="Ready", vessel_count=2, part_count=9,
         target_set=True, target_distance=8.0),                               # -> DOCK
    snap(ut=419.0, docking_state="Ready", vessel_count=2, part_count=9,
         target_set=True, target_distance=8.0),                               # deferred enable
    snap(ut=430.0, docking_state="Ready", vessel_count=2, part_count=9,
         target_set=True, target_distance=4.0, mj_docking_enabled=True),
    snap(ut=470.0, docking_state="Docked", vessel_count=1, part_count=32,
         target_set=True, target_distance=0.3, mj_docking_enabled=False),     # -> RD-SETTLE
    snap(ut=481.0, docking_state="Docked", vessel_count=1, part_count=32),    # -> RD-TERMINAL
]


class RDockSeparatedStartTests(unittest.TestCase):
    """`startSeparated = true` (LAB-13b): the pair is already split by the spec's seam
    steps, so the machine switches to the other half, controls it from its port, targets
    the half named craft_name and docks. RD-UNDOCK is never entered."""

    def test_the_default_is_the_undock_machine(self):
        self.assertFalse(mlib.RDockParams().start_separated)
        self.assertFalse(mlib.rdock_params_from_dict({}).start_separated)
        self.assertEqual(mlib.rdock_params_from_dict({}).switch_timeout, 60.0)
        p = mlib.rdock_params_from_dict({"startSeparated": True,
                                         "switchTimeoutSeconds": 30})
        self.assertEqual((p.start_separated, p.switch_timeout), (True, 30.0))

    def test_happy_path_switches_controls_targets_and_docks(self):
        st, log = _walk(SEP_HAPPY, SEP_PARAMS)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict, st.flake_reason)
        self.assertEqual(st.phases_reached, (
            mlib.RDOCK_START, mlib.RDOCK_SWITCH, mlib.RDOCK_BACKOFF, mlib.RDOCK_TARGET,
            mlib.BDOCK_DOCK, mlib.RDOCK_SETTLE, mlib.RDOCK_TERMINAL))
        self.assertNotIn(mlib.RDOCK_UNDOCK, st.phases_reached)
        kinds = [k for _p, ks in log for k in ks]
        self.assertNotIn(mlib.ACTION_UNDOCK, kinds)
        self.assertIn((mlib.RDOCK_SWITCH, [mlib.ACTION_SWITCH_TO_NEAREST_OTHER_VESSEL]), log)
        self.assertIn((mlib.RDOCK_BACKOFF, [mlib.ACTION_CONTROL_FROM_DOCKING_PORT]), log)
        self.assertIn((mlib.RDOCK_TARGET, [mlib.ACTION_TARGET_NEAREST_NAMED_VESSEL]), log)
        self.assertIn((mlib.BDOCK_DOCK, [mlib.ACTION_SET_SAS, mlib.ACTION_SET_RCS,
                                         mlib.ACTION_SET_TARGET_DOCKING_PORT]), log)
        self.assertIn((mlib.RDOCK_SETTLE, [mlib.ACTION_MJ_DISABLE_DOCKING]), log)
        outcomes = mlib.evaluate_rdock_assertions([], SEP_PARAMS, st.phases_reached, st)
        self.assertEqual([o.name for o in outcomes],
                         ["startedSeparated", "switched", "redocked"])
        self.assertTrue(all(o.met for o in outcomes), outcomes)

    def test_the_switch_skips_the_craft_name_and_the_target_is_the_craft(self):
        st = mlib.rdock_initial_state(SEP_PARAMS)
        st, actions = mlib.rdock_decide(st, snap(ut=0.0, docking_state="Ready",
                                                 part_count=23))
        self.assertEqual((st.phase, actions), (mlib.RDOCK_START, []))
        st, actions = mlib.rdock_decide(st, snap(ut=5.0, docking_state="Ready",
                                                 part_count=23))
        self.assertEqual(st.phase, mlib.RDOCK_SWITCH)
        self.assertEqual(st.switch_baseline_part_count, 23)
        self.assertEqual([(a.kind, a.text) for a in actions],
                         [(mlib.ACTION_SWITCH_TO_NEAREST_OTHER_VESSEL, LAB)])
        st, _ = mlib.rdock_decide(st, snap(ut=6.0, docking_state="Ready", part_count=9))
        st, actions = mlib.rdock_decide(st, snap(ut=16.0, docking_state="Ready",
                                                 part_count=9))
        self.assertEqual(st.phase, mlib.RDOCK_TARGET)
        self.assertEqual([(a.kind, a.text) for a in actions],
                         [(mlib.ACTION_TARGET_NEAREST_NAMED_VESSEL, LAB)])

    def test_a_mated_or_unread_port_never_starts_and_flakes_named(self):
        for state in ("Docked", "Docking", "PreAttached", ""):
            st = mlib.rdock_initial_state(SEP_PARAMS)
            for ut in (0.0, 30.0, 59.0):
                st, actions = mlib.rdock_decide(st, snap(ut=ut, docking_state=state,
                                                         part_count=32))
                self.assertEqual((st.phase, actions), (mlib.RDOCK_START, []), state)
            st, actions = mlib.rdock_decide(st, snap(ut=61.0, docking_state=state,
                                                     part_count=32))
            self.assertEqual(st.verdict, mlib.MISSION_FLAKE, state)
            self.assertIn("never read a separated port", st.flake_reason)
            self.assertEqual(actions, [])

    def test_an_undocking_port_reads_separated(self):
        # kRPC reports a port leaving Disengage as `undocking`.
        st = mlib.rdock_initial_state(SEP_PARAMS)
        st, _ = mlib.rdock_decide(st, snap(ut=0.0, docking_state="Undocking", part_count=23))
        st, _ = mlib.rdock_decide(st, snap(ut=5.0, docking_state="Undocking", part_count=23))
        self.assertEqual(st.phase, mlib.RDOCK_SWITCH)

    def test_an_unread_part_count_does_not_start(self):
        st = mlib.rdock_initial_state(SEP_PARAMS)
        st, _ = mlib.rdock_decide(st, snap(ut=0.0, docking_state="Ready", part_count=0))
        st, actions = mlib.rdock_decide(st, snap(ut=10.0, docking_state="Ready",
                                                 part_count=0))
        self.assertEqual((st.phase, actions), (mlib.RDOCK_START, []))

    def test_a_switch_that_never_shows_flakes_named(self):
        st, _ = _walk(SEP_HAPPY[:2], SEP_PARAMS)
        self.assertEqual(st.phase, mlib.RDOCK_SWITCH)
        for parts in (23, 0):
            st, actions = mlib.rdock_decide(st, snap(ut=440.0, docking_state="Ready",
                                                     part_count=parts))
            self.assertEqual((st.phase, actions), (mlib.RDOCK_SWITCH, []))
        st, actions = mlib.rdock_decide(st, snap(ut=466.0, docking_state="Ready",
                                                 part_count=23))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(st.flake_phase, mlib.RDOCK_SWITCH)
        self.assertIn("no switch to the other half observed", st.flake_reason)
        self.assertIn(LAB, st.flake_reason)
        outcomes = {o.name: o.met for o in mlib.evaluate_rdock_assertions(
            [], SEP_PARAMS, st.phases_reached, st)}
        self.assertEqual(outcomes, {"startedSeparated": True, "switched": False,
                                    "redocked": False})

    def test_the_default_machine_ignores_the_separated_gate(self):
        # Default off: a Ready port at start is NOT a start (the undock machine waits
        # for Docked) - the existing lanes' behaviour, unchanged.
        st = mlib.rdock_initial_state(mlib.RDockParams(start_settle_seconds=0.0))
        st, actions = mlib.rdock_decide(st, snap(ut=0.0, docking_state="Ready",
                                                 vessel_count=2, part_count=23))
        self.assertEqual((st.phase, actions), (mlib.RDOCK_START, []))


class PickNearestOtherTests(unittest.TestCase):

    def test_skips_the_active_vessel_the_skipped_name_and_unread_distances(self):
        pick = mlib.pick_nearest_other
        nan = float("nan")
        self.assertEqual(pick([(LAB, 0.0, True), ("LABX Orbital Lab Probe", 7.0, False)],
                              LAB), 1)
        # A same-named copy beside the active one is skipped; the nearest other wins.
        self.assertEqual(pick([(LAB, 2.0, False), ("A", 9.0, False), ("B", 4.0, False)],
                              LAB), 2)
        self.assertIsNone(pick([(LAB, 0.0, True), (LAB, 3.0, False)], LAB))
        self.assertIsNone(pick([("A", nan, False)], LAB))
        self.assertIsNone(pick([], LAB))
        self.assertIsNone(pick(None, LAB))
        # An empty skip name skips no name.
        self.assertEqual(pick([(LAB, 0.0, True), (LAB, 3.0, False)], ""), 1)


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

    def test_shell_flies_the_separated_start_to_mission_ok(self):
        control = FakeMissionControl(SEP_HAPPY)
        params = {"craftName": LAB, "startSeparated": True, "startSettleSeconds": 5,
                  "undockTimeoutSeconds": 120, "backoffSeconds": 10,
                  "dockSpeedMetersPerSec": 0.5, "dockTimeoutSeconds": 900}
        code, result = run(d5_redock.SPEC, params, control, budget=9000.0)
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

    def test_the_separated_spec_names_this_mission_and_satisfies_the_schema(self):
        with open(SEPARATED_SPEC_PATH, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual(spec["driver"]["mission"], "d5_redock")
        self.assertEqual(spec["fixture"]["saveTemplate"], "fixtures/saves/lab-lko-tender-rcs")
        with open(SCHEMA_PATH, "rb") as fh:
            schema = tomllib.load(fh)["params"]
        params = spec["driver"]["missionParams"]
        for key, rule in schema.items():
            if rule.get("required"):
                self.assertIn(key, params, key)
        for key in params:
            self.assertIn(key, schema, "undeclared mission param %s" % key)
        self.assertIs(params["startSeparated"], True)
        self.assertEqual(params["craftName"], LAB)
        p = mlib.rdock_params_from_dict(params)
        self.assertTrue(p.start_separated)

    def test_the_separated_specs_first_post_mission_warp_is_past_the_mission_bound(self):
        # On MISSION-OK the mission ends no later than its start plus every phase's
        # game-time budget (it never warps), so the first post-mission WarpToUT can
        # never be a backward warp (`warptout refused`).
        with open(SEPARATED_SPEC_PATH, "rb") as fh:
            spec = tomllib.load(fh)
        steps = spec["driver"]["steps"]
        m = next(i for i, s in enumerate(steps) if s.get("phase") == "mission")
        pre = [float(s["args"]["ut"]) for s in steps[:m] if s.get("cmd") == "WarpToUT"]
        post = next(float(s["args"]["ut"]) for s in steps[m + 1:]
                    if s.get("cmd") == "WarpToUT")
        p = mlib.rdock_params_from_dict(spec["driver"]["missionParams"])
        bound = (max(pre) + p.start_timeout + p.switch_timeout + p.backoff_seconds
                 + p.target_timeout + p.bdock.dock_timeout + p.settle_seconds)
        self.assertGreater(post, bound + 30.0)

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
