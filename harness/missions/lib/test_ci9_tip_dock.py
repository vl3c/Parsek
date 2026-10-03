"""Unit cells for the dock with a Real-Spawned chain tip (mission `ci9_tip_dock`,
scenario `CI-9-chain-tip-dock`, fixture `eva2-lko-crewed` + the `chain-tip-dock`
preset).

Three groups:

  1. THE PURE MACHINE (`mlib.tdock_decide`): the settle / target order, the
     distance-keyed hand-off (beyond the approach distance -> B-DOCK RENDEZVOUS, inside
     it -> MATCH-VELOCITY), the delegated phases and B-DOCK's corroborated docked
     completion, and the named give-ups.
  2. THE SHELL, driven end to end over a scripted flight with no krpc and no KSP.
  3. SPEC / SCHEMA SYNC, and the fixture shape the lane depends on: the booted active
     vessel carries a Ready docking port and RCS.

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
import ci9_tip_dock                # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "CI-9-chain-tip-dock.toml")
SCHEMA_PATH = os.path.join(_MISSIONS, "ci9_tip_dock.schema.toml")
FIXTURE_SFS = os.path.join(_HARNESS, "fixtures", "saves", "eva2-lko-crewed",
                           "persistent.sfs")

PARAMS = mlib.TDockParams(
    bdock=mlib.BDockParams(approach_distance=100.0, max_phasing_orbits=5.0),
    target_name="CTD Target", start_settle_seconds=5.0, settle_seconds=10.0)

# A spawn 120 km away (what the RealSpawn jump measured): rendezvous, match, dock.
FAR = [
    snap(ut=500.0, altitude=100000.0),                                  # TD-START
    snap(ut=505.0, altitude=100001.0),                                  # -> TD-TARGET
    snap(ut=506.0, altitude=100002.0, target_set=True,
         target_distance=120392.0),                                     # -> RENDEZVOUS
    snap(ut=507.0, altitude=100003.0, target_set=True, target_distance=120000.0,
         mj_rendezvous_enabled=True),
    snap(ut=900.0, altitude=100100.0, target_set=True, target_distance=99.0,
         target_rel_speed=0.2, mj_rendezvous_enabled=False),            # -> MATCH
    snap(ut=901.0, altitude=100101.0, target_set=True, target_distance=99.0,
         target_rel_speed=0.1),                                         # -> DOCK
    snap(ut=902.0, altitude=100102.0, target_set=True, target_distance=99.0,
         target_rel_speed=0.1),                                         # deferred enable
    snap(ut=903.0, altitude=100103.0, target_set=True, target_distance=90.0,
         mj_docking_enabled=True),
    snap(ut=1100.0, altitude=100150.0, docking_state="Docked", target_set=True,
         target_distance=0.4, mj_docking_enabled=False),                # -> TD-SETTLE
    snap(ut=1105.0, altitude=100151.0, docking_state="Docked"),
    snap(ut=1111.0, altitude=100152.0, docking_state="Docked"),         # -> TD-TERMINAL
]


def _walk(frames, params=PARAMS):
    st = mlib.tdock_initial_state(params)
    log = []
    for f in frames:
        st, actions = mlib.tdock_decide(st, f)
        log.append((st.phase, [a.kind for a in actions]))
        if st.done:
            break
    return st, log


class TDockMachineTests(unittest.TestCase):

    def test_far_spawn_rendezvous_then_docks_in_order(self):
        st, log = _walk(FAR)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(st.phases_reached, (
            mlib.TDOCK_START, mlib.TDOCK_TARGET, mlib.BDOCK_RENDEZVOUS,
            mlib.BDOCK_MATCH_VELOCITY, mlib.BDOCK_DOCK, mlib.TDOCK_SETTLE,
            mlib.TDOCK_TERMINAL))
        self.assertEqual(log[1], (mlib.TDOCK_TARGET, [
            mlib.ACTION_SET_SAS, mlib.ACTION_SET_RCS,
            mlib.ACTION_TARGET_NEAREST_NAMED_VESSEL]))
        self.assertEqual(log[2], (mlib.BDOCK_RENDEZVOUS, [mlib.ACTION_MJ_ENABLE_RENDEZVOUS]))
        self.assertIn((mlib.BDOCK_MATCH_VELOCITY, [mlib.ACTION_MJ_KILL_REL_VEL]), log)
        self.assertIn((mlib.BDOCK_DOCK, [mlib.ACTION_MJ_ENABLE_DOCKING]), log)
        kinds = [k for _p, ks in log for k in ks]
        self.assertNotIn(mlib.ACTION_START_RESOURCE_TRANSFER, kinds)
        self.assertIn((mlib.TDOCK_SETTLE, [mlib.ACTION_MJ_DISABLE_DOCKING]), log)
        self.assertTrue(st.rendezvous_needed)
        self.assertEqual(st.acquired_target_distance, 120392.0)
        outcomes = {o.name: o for o in mlib.evaluate_tdock_assertions(
            [], PARAMS, st.phases_reached, st)}
        self.assertTrue(all(o.met for o in outcomes.values()), outcomes)
        self.assertEqual(outcomes["targetAcquired"].value, 120392.0)

    def test_the_rendezvous_enable_carries_the_approach_distance_and_orbit_limit(self):
        st, _ = _walk(FAR[:2])
        st, actions = mlib.tdock_decide(st, FAR[2])
        self.assertEqual([(a.kind, a.value, a.limit) for a in actions],
                         [(mlib.ACTION_MJ_ENABLE_RENDEZVOUS, 100.0, 5.0)])
        self.assertEqual(st.inner.phase, mlib.BDOCK_RENDEZVOUS)

    def test_a_spawn_where_its_ghost_stood_skips_the_rendezvous(self):
        # The design's jump contract (the tip spawns where its ghost stood, ~140 m away):
        # inside rendezvousAboveMeters, so the docking AP closes directly.
        st, _ = _walk(FAR[:2])
        st, actions = mlib.tdock_decide(st, snap(ut=506.0, target_set=True,
                                                 target_distance=140.0))
        self.assertEqual(st.phase, mlib.BDOCK_MATCH_VELOCITY)
        self.assertEqual([a.kind for a in actions], [mlib.ACTION_MJ_KILL_REL_VEL])
        self.assertFalse(st.rendezvous_needed)
        self.assertNotIn(mlib.BDOCK_RENDEZVOUS, st.phases_reached)

    def test_the_target_action_names_the_target(self):
        st, _ = _walk(FAR[:1])
        st, actions = mlib.tdock_decide(st, FAR[1])
        self.assertEqual(st.phase, mlib.TDOCK_TARGET)
        self.assertIn((mlib.ACTION_TARGET_NEAREST_NAMED_VESSEL, "CTD Target"),
                      [(a.kind, a.text) for a in actions])

    def test_the_target_waits_for_the_start_dwell(self):
        st = mlib.tdock_initial_state(PARAMS)
        for ut in (500.0, 504.9):
            st, actions = mlib.tdock_decide(st, snap(ut=ut))
            self.assertEqual((st.phase, actions), (mlib.TDOCK_START, []))

    def test_an_unread_distance_never_hands_off(self):
        st, _ = _walk(FAR[:2])
        st, actions = mlib.tdock_decide(st, snap(ut=506.0, target_set=True,
                                                 target_distance=float("nan")))
        self.assertEqual((st.phase, actions), (mlib.TDOCK_TARGET, []))

    def test_target_give_up_is_named(self):
        st, _ = _walk(FAR[:2])
        st, _ = mlib.tdock_decide(st, snap(ut=566.0, target_set=False))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no target acquired on a vessel named 'CTD Target'", st.flake_reason)

    def test_a_delegated_flake_surfaces_bdocks_reason(self):
        st, _ = _walk(FAR[:4])
        self.assertEqual(st.phase, mlib.BDOCK_RENDEZVOUS)
        st, _ = mlib.tdock_decide(st, snap(ut=507.0 + 30001.0, altitude=100500.0,
                                           target_set=True, target_distance=50000.0,
                                           mj_rendezvous_enabled=True, node_count=1))
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(st.flake_phase, mlib.BDOCK_RENDEZVOUS)
        outcomes = {o.name: o.met for o in mlib.evaluate_tdock_assertions(
            [], PARAMS, st.phases_reached, st)}
        self.assertEqual(outcomes, {"targetAcquired": True, "docked": False})

    def test_the_rendezvous_threshold_is_inclusive_at_its_edge(self):
        for d, phase in ((250.0, mlib.BDOCK_MATCH_VELOCITY), (250.1, mlib.BDOCK_RENDEZVOUS)):
            st, _ = _walk(FAR[:2])
            st, _ = mlib.tdock_decide(st, snap(ut=506.0, target_set=True, target_distance=d))
            self.assertEqual(st.phase, phase, d)

    def test_params_from_dict(self):
        p = mlib.tdock_params_from_dict({
            "targetName": "X", "startSettleSeconds": 7, "targetTimeoutSeconds": 4,
            "settleSeconds": 5, "approachDistanceMeters": 120,
            "rendezvousAboveMeters": 300,
            "maxPhasingOrbits": 3, "dockSpeedMetersPerSec": 0.4})
        self.assertEqual((p.target_name, p.start_settle_seconds, p.target_timeout,
                          p.settle_seconds, p.rendezvous_above), ("X", 7.0, 4.0, 5.0, 300.0))
        self.assertEqual((p.bdock.approach_distance, p.bdock.max_phasing_orbits,
                          p.bdock.dock_speed), (120.0, 3.0, 0.4))


class TDockShellTests(unittest.TestCase):

    def test_shell_flies_the_scripted_mission_to_mission_ok(self):
        control = FakeMissionControl(FAR)
        params = {"targetName": "CTD Target", "startSettleSeconds": 5,
                  "approachDistanceMeters": 100, "maxPhasingOrbits": 5,
                  "matchSpeedMetersPerSec": 1.0, "dockSpeedMetersPerSec": 0.5,
                  "rendezvousTimeoutSeconds": 30000, "dockTimeoutSeconds": 1800}
        code, result = run(ci9_tip_dock.SPEC, params, control, budget=9000.0)
        self.assertEqual(result["mission"], "ci9_tip_dock")
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)


class TDockSpecSyncTests(unittest.TestCase):

    def _spec(self):
        with open(SPEC_PATH, "rb") as fh:
            return tomllib.load(fh)

    def test_spec_names_this_mission_and_satisfies_the_schema(self):
        spec = self._spec()
        self.assertEqual(spec["driver"]["mission"], "ci9_tip_dock")
        with open(SCHEMA_PATH, "rb") as fh:
            schema = tomllib.load(fh)["params"]
        params = spec["driver"]["missionParams"]
        for key, rule in schema.items():
            if rule.get("required"):
                self.assertIn(key, params, key)
        for key in params:
            self.assertIn(key, schema, "undeclared mission param %s" % key)
        self.assertEqual(params["targetName"], "CTD Target")

    def test_the_booted_active_vessel_has_a_ready_port_and_rcs(self):
        spec = self._spec()
        self.assertEqual(spec["fixture"]["saveTemplate"], "fixtures/saves/eva2-lko-crewed")
        self.assertEqual(spec["fixture"]["injectedRecordings"], "chain-tip-dock")
        with open(FIXTURE_SFS, "r", encoding="utf-8") as fh:
            text = fh.read().replace("\r\n", "\n")
        flight = text[text.index("\tFLIGHTSTATE"):]
        active = int(re.search(r"\n\t\tactiveVessel = (\d+)\n", flight).group(1))
        vessel = re.split(r"\n\t\tVESSEL\n", flight)[1:][active]
        self.assertIn("\n\t\t\tname = Kerbal X\n", vessel)
        self.assertIn("\n\t\t\tpersistentId = 3620499050\n", vessel)
        states = re.findall(r"\n\t+name = ModuleDockingNode\n(?:.*\n)*?\t+state = (.*)\n",
                            vessel)
        self.assertEqual([s.strip() for s in states], ["Ready"])
        self.assertIn("name = RCSBlock.v2", vessel)


if __name__ == "__main__":
    unittest.main()
