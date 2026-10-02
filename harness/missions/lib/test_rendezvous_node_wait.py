"""Unit tests for RENDEZVOUS NODE WAITS (BDOCK family, operator ruling
2026-10-02: rails warp only).

MechJeb's rendezvous autopilot hands every node to the shared NodeExecutor,
which rails-warps to ignition - 600 s and then idles at 1x until the craft is
aligned AND settled; the BDOCK Interceptor never settles, so each rendezvous
burn waited ~600 s at 1x. The hold turns the executor's autowarp off,
rails-warps itself to the node-wait target (clamped so the target vessel stays
outside RV_WARP_MIN_TARGET_DISTANCE_M) and turns autowarp back on at 1x.

Runnable with the stdlib runner only::

    cd harness && python -m unittest discover -s missions/lib -q
"""

import math
import os
import sys
import types
import unittest
from dataclasses import replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
for _p in (_MISSIONS, _HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import mission_runner  # noqa: E402
import mlib  # noqa: E402
from mlib import Action  # noqa: E402
from test_mlib import snap  # noqa: E402


def kinds(actions):
    return [a.kind for a in actions]


# The 2026-09-30_1732 BDOCK-1 first rendezvous node, as the machine read it at
# the frame the executor parked at 1x (lead ~602 s): a 17.172 m/s node at
# ut 7720.461 on a 250 kN stage. 10 t is a stand-in mass (~0.34 s half burn).
NODE_UT = 7720.461
NODE_DV = 17.172
INPUTS = dict(available_thrust=250_000.0, vessel_mass=10_000.0,
              atmosphere_depth=70_000.0, time_to_soi=float("nan"))
HALF = mlib.half_burn_seconds(NODE_DV, 250_000.0, 10_000.0)
PLAN_TARGET = NODE_UT - HALF - mlib.NODE_WAIT_ORIENT_LEAD_SECONDS


def rv_snap(ut, **kw):
    """A RENDEZVOUS frame with the executor parked at 1x on the first node,
    the target 40 km away and no closer than 15 km over the next orbit."""
    base = dict(ut=ut, mj_rendezvous_enabled=True, node_count=1,
                node_ut=NODE_UT, node_dv=NODE_DV, altitude=94_000.0,
                apoapsis=95_420.6, periapsis=84_359.5, warp_mode="NONE",
                target_distance=40_000.0, target_ca_ut=7900.0,
                target_ca_distance=15_000.0, situation="ORBITING", body="Kerbin",
                **INPUTS)
    base.update(kw)
    return snap(**base)


def rv_state(**kw):
    st = mlib.bdock_initial_state(mlib.BDockParams())
    st = replace(st, phase=mlib.BDOCK_RENDEZVOUS, phase_entry_ut=696.8,
                 phases_reached=st.phases_reached + (mlib.BDOCK_RENDEZVOUS,),
                 rendezvous_ever_enabled=True, rendezvous_min_distance=math.inf)
    return replace(st, **kw)


def drive(state, frames):
    out = []
    for f in frames:
        state, actions = mlib.bdock_decide(state, f)
        out.append(actions)
    return state, out


def armed(t0=7118.6):
    """The first frame seeds the apsides; two more static frames at 1x make
    RV_WARP_IDLE_FRAMES, and the hold arms on the third."""
    st, acts = drive(rv_state(), [rv_snap(t0), rv_snap(t0 + 0.5), rv_snap(t0 + 1.0)])
    return st, acts


class CaLimitTests(unittest.TestCase):

    def test_unread_or_near_target_refuses(self):
        nan = float("nan")
        self.assertEqual("target-unread",
                         mlib.rendezvous_ca_warp_limit(0.0, nan, 100.0, 50.0, 10.0)[1])
        lim, key, _ = mlib.rendezvous_ca_warp_limit(0.0, 4999.0, 100.0, 9000.0, 10.0)
        self.assertIsNone(lim)
        self.assertEqual("target-near", key)
        self.assertEqual("ca-unread",
                         mlib.rendezvous_ca_warp_limit(0.0, 9000.0, nan, nan, 10.0)[1])

    def test_closest_approach_outside_the_safe_distance_is_unlimited(self):
        lim, key, _ = mlib.rendezvous_ca_warp_limit(0.0, 40_000.0, 500.0, 5000.0, 10.0)
        self.assertEqual(math.inf, lim)
        self.assertEqual("ca-clear", key)

    def test_near_closest_approach_clamps_with_the_slower_speed(self):
        """BDOCK-1's second node shape: 100 m at the closest approach, the node
        dv (the match-velocities dv there) slower than the secant. MUTATION:
        take max() of the two speeds and the guard shrinks, the clamp lands
        later, and this reds."""
        now, dist, ca_ut, ca_d, dv = 8002.0, 30_000.0, 8603.0, 100.0, 15.656
        lim, key, detail = mlib.rendezvous_ca_warp_limit(now, dist, ca_ut, ca_d, dv)
        self.assertEqual("ca-clamp", key)
        guard = math.sqrt(5000.0 ** 2 - 100.0 ** 2) / dv
        self.assertAlmostEqual(ca_ut - guard, lim, places=3)
        self.assertIn("guard=319", detail)
        # A slow secant wins over a fast node dv.
        lim2, _, _ = mlib.rendezvous_ca_warp_limit(now, 6000.0, ca_ut, ca_d, 50.0)
        v_secant = math.sqrt(6000.0 ** 2 - 100.0 ** 2) / (ca_ut - now)
        self.assertAlmostEqual(ca_ut - math.sqrt(5000.0 ** 2 - 100.0 ** 2) / v_secant,
                               lim2, places=3)

    def test_unread_node_dv_falls_back_to_the_secant(self):
        lim, key, _ = mlib.rendezvous_ca_warp_limit(0.0, 30_000.0, 600.0, 100.0,
                                                    float("nan"))
        self.assertEqual("ca-clamp", key)
        v = math.sqrt(30_000.0 ** 2 - 100.0 ** 2) / 600.0
        self.assertAlmostEqual(600.0 - math.sqrt(5000.0 ** 2 - 100.0 ** 2) / v, lim, places=3)

    def test_closest_approach_now_refuses(self):
        self.assertIsNone(mlib.rendezvous_ca_warp_limit(10.0, 6000.0, 10.0, 100.0, 5.0)[0])


class PlanTests(unittest.TestCase):

    def plan(self, now=7118.6, **kw):
        args = dict(node_ut=NODE_UT, node_dv=NODE_DV, available_thrust=250_000.0,
                    vessel_mass=10_000.0, time_to_soi=float("nan"),
                    altitude=94_000.0, periapsis=84_359.5,
                    atmosphere_depth=70_000.0, distance=40_000.0,
                    ca_ut=7900.0, ca_distance=15_000.0, body="Kerbin")
        args.update(kw)
        return mlib.rendezvous_node_wait_plan(now, **args)

    def test_far_node_warps_to_lead_plus_half_burn_before_it(self):
        target, key, detail = self.plan()
        self.assertEqual("held", key)
        self.assertAlmostEqual(PLAN_TARGET, target, places=3)
        self.assertIn("node-wait", detail)

    def test_near_closest_approach_clamps_the_target(self):
        target, key, detail = self.plan(now=8002.0, node_ut=8603.0, node_dv=15.656,
                                        distance=30_000.0, ca_ut=8603.0,
                                        ca_distance=100.0)
        self.assertEqual("held", key)
        self.assertAlmostEqual(8603.0 - math.sqrt(5000.0 ** 2 - 100.0 ** 2) / 15.656,
                               target, places=3)
        self.assertIn("ca-clamp", detail)

    def test_short_window_after_the_clamp_refuses(self):
        target, key, _ = self.plan(now=8250.0, node_ut=8603.0, node_dv=15.656,
                                   distance=10_000.0, ca_ut=8603.0, ca_distance=100.0)
        self.assertIsNone(target)
        self.assertEqual("window-short", key)

    def test_window_cap(self):
        target, _, detail = self.plan(now=1000.0)
        self.assertAlmostEqual(1000.0 + mlib.RV_WARP_MAX_WINDOW_SECONDS, target)
        self.assertIn("window-capped", detail)

    def test_atmosphere_and_unread_inputs_refuse(self):
        self.assertEqual("node-plan", self.plan(periapsis=60_000.0)[1])
        self.assertEqual("node-plan", self.plan(vessel_mass=float("nan"))[1])
        self.assertEqual("target-near", self.plan(distance=3000.0)[1])
        self.assertEqual("ca-unread", self.plan(ca_ut=float("nan"))[1])

    def test_rails_only_refuses_where_warp_to_would_physics_warp(self):
        """kRPC WarpTo falls back to physics warp below the factor-1 rails
        limit. Mun: 5 km. MUTATION: drop the periapsis from the min() and the
        second cell reds."""
        airless = dict(atmosphere_depth=0.0, body="Mun")
        self.assertEqual("rails-illegal", self.plan(altitude=4000.0, periapsis=4000.0,
                                                    **airless)[1])
        self.assertEqual("rails-illegal", self.plan(altitude=30_000.0, periapsis=4000.0,
                                                    **airless)[1])
        self.assertEqual("held", self.plan(altitude=30_000.0, periapsis=20_000.0,
                                           **airless)[1])
        self.assertEqual("rails-unknown", self.plan(body="")[1])


class HoldMachineTests(unittest.TestCase):

    def test_arms_on_the_second_idle_frame_autowarp_off_first(self):
        """Order is load-bearing: with autowarp still on, the executor's
        StateWarpAlign calls MinimumWarp every tick and cancels the warp."""
        st, acts = armed()
        self.assertEqual([[], []], acts[:2])
        self.assertEqual([mlib.ACTION_MJ_SET_NODE_AUTOWARP, mlib.ACTION_WARP_TO_UT],
                         kinds(acts[2]))
        self.assertEqual(0.0, acts[2][0].value)
        self.assertAlmostEqual(PLAN_TARGET, acts[2][1].value, places=3)
        self.assertIn("node-wait", acts[2][1].text)
        self.assertAlmostEqual(PLAN_TARGET, st.rv_hold_ut, places=3)
        self.assertEqual("held", st.rv_warp_key)

    def test_never_arms_while_burning_or_while_the_executor_warps(self):
        st, acts = drive(rv_state(), [rv_snap(7118.6), rv_snap(7119.1, apoapsis=95_500.0),
                                      rv_snap(7119.6, apoapsis=95_600.0)])
        self.assertEqual([[], [], []], acts)
        st, acts = drive(rv_state(), [rv_snap(t, warp_mode="RAILS", warp_rate=50.0)
                                      for t in (5000.0, 5025.0, 5050.0)])
        self.assertEqual([[], [], []], acts)
        self.assertIsNone(st.rv_hold_ut)

    def test_no_node_or_ap_off_or_switch_off_never_arms(self):
        for kw in (dict(node_count=0, node_ut=float("nan")),
                   dict(mj_rendezvous_enabled=False)):
            st, acts = drive(rv_state(), [rv_snap(7118.6 + i * 0.5, **kw)
                                          for i in range(4)])
            self.assertTrue(all(a == [] for a in acts), kw)
        off = replace(mlib.BDockParams(), rendezvous_node_wait_warp=False)
        st, acts = drive(rv_state(params=off), [rv_snap(7118.6 + i * 0.5)
                                                for i in range(4)])
        self.assertEqual([[], [], [], []], acts)

    def test_quiet_while_warping_then_released_at_1x(self):
        st, _ = armed()
        st, acts = drive(st, [rv_snap(7300.0, warp_mode="RAILS", warp_rate=50.0,
                                      warping_to=PLAN_TARGET)])
        self.assertEqual([[]], acts)
        st, acts = drive(st, [rv_snap(PLAN_TARGET - 1.0)])
        self.assertEqual([mlib.ACTION_MJ_SET_NODE_AUTOWARP], kinds(acts[0]))
        self.assertEqual(1.0, acts[0][0].value)
        self.assertIn("arrived", acts[0][0].text)
        self.assertIsNone(st.rv_hold_ut)
        self.assertAlmostEqual(NODE_UT, st.rv_spent_node_ut)
        # The same node is never held twice.
        st, acts = drive(st, [rv_snap(PLAN_TARGET + 1.0), rv_snap(PLAN_TARGET + 2.0)])
        self.assertEqual([[], []], acts)

    def test_release_while_still_on_rails_waits_for_1x_before_autowarp(self):
        """On rails StateWarpAlign uses a 10 deg cone and would warp itself to
        ignition - 3 s. MUTATION: emit autowarp-on with the cancel and this reds."""
        st, _ = armed()
        st, acts = drive(st, [rv_snap(PLAN_TARGET - 2.0, warp_mode="RAILS",
                                      warp_rate=5.0, warping_to=PLAN_TARGET)])
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(acts[0]))
        self.assertTrue(st.rv_hold_releasing)
        st, acts = drive(st, [rv_snap(PLAN_TARGET + 0.5)])
        self.assertEqual([mlib.ACTION_MJ_SET_NODE_AUTOWARP], kinds(acts[0]))
        self.assertIsNone(st.rv_hold_ut)

    def test_target_closing_inside_the_safe_distance_releases(self):
        st, _ = armed()
        st, acts = drive(st, [rv_snap(7400.0, target_distance=4800.0, warp_mode="RAILS",
                                      warping_to=PLAN_TARGET)])
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(acts[0]))
        self.assertIn("target-near", acts[0][0].text)

    def test_node_gone_moved_or_ap_off_releases(self):
        st, _ = armed()
        st, acts = drive(st, [rv_snap(7400.0, warp_mode="PHYSICS", warp_rate=2.0)])
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(acts[0]))
        self.assertIn("physics-warp", acts[0][0].text)
        for kw, word in ((dict(node_count=0, node_ut=float("nan")), "node-gone"),
                         (dict(node_ut=NODE_UT + 30.0), "node-moved"),
                         (dict(mj_rendezvous_enabled=False), "ap-off")):
            st, _ = armed()
            st, acts = drive(st, [rv_snap(7400.0, **kw)])
            self.assertEqual([mlib.ACTION_MJ_SET_NODE_AUTOWARP], kinds(acts[0]), kw)
            self.assertIn(word, acts[0][0].text)

    def test_self_heal_then_give_up_after_the_issue_cap(self):
        st, _ = armed()
        st, acts = drive(st, [rv_snap(7119.6 + mlib.WARP_REISSUE_SECONDS + 1.0)])
        self.assertEqual([mlib.ACTION_WARP_TO_UT], kinds(acts[0]))
        st = replace(st, rv_hold_issues=mlib.RV_WARP_MAX_ISSUES)
        st, acts = drive(st, [rv_snap(7300.0)])
        self.assertEqual([mlib.ACTION_MJ_SET_NODE_AUTOWARP], kinds(acts[0]))
        self.assertIn("warp-not-taking", acts[0][0].text)

    def test_terminal_while_held_cancels_and_leaves_autowarp_alone(self):
        st, _ = armed()
        st, acts = drive(st, [rv_snap(7300.0, vessel_lost=True, warp_mode="RAILS",
                                      warping_to=PLAN_TARGET)])
        self.assertTrue(st.done)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(acts[0]))
        self.assertIsNone(st.rv_hold_ut)

    def test_budget_flake_while_held_cancels_and_drops_any_warp_issue(self):
        st, _ = armed()
        late = 696.8 + mlib.BDockParams().rendezvous_timeout + 1.0
        st, acts = drive(st, [rv_snap(late)])
        self.assertTrue(st.done)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(acts[0]))

    def test_second_node_is_clamped_by_the_closest_approach(self):
        st, _ = armed()
        st, _ = drive(st, [rv_snap(PLAN_TARGET)])
        n2 = dict(node_ut=8603.0, node_dv=15.656, target_distance=30_000.0,
                  target_ca_ut=8603.0, target_ca_distance=100.0)
        n2["apoapsis"] = 99_000.0  # the first burn changed the orbit
        st, acts = drive(st, [rv_snap(7721.0, **n2), rv_snap(8002.0, **n2),
                              rv_snap(8002.5, **n2)])
        self.assertEqual([[], []], acts[:2])
        self.assertEqual(mlib.ACTION_WARP_TO_UT, acts[2][1].kind)
        self.assertLess(acts[2][1].value, 8603.0 - 300.0)
        self.assertIn("ca-clamp", acts[2][1].text)

    def test_a_repeated_decline_prints_one_decision_line(self):
        st, acts = drive(rv_state(), [rv_snap(7118.6 + i * 0.5, target_distance=3000.0)
                                      for i in range(6)])
        self.assertTrue(all(a == [] for a in acts))
        prev = rv_state()
        lines = []
        for i in range(6):
            new, _ = mlib.bdock_decide(prev, rv_snap(7118.6 + i * 0.5,
                                                     target_distance=3000.0))
            lines += [c for c in mlib.diff_machine_state(prev, new)
                      if c.startswith("rvWarp")]
            prev = new
        self.assertEqual(1, len(lines), lines)
        self.assertIn("declined:target-near", lines[0])

    def test_sdock_inherits_the_hold(self):
        """BDOCK-2's machine delegates RENDEZVOUS to bdock_decide."""
        self.assertTrue(hasattr(mlib.bdock_decide, "__wrapped__"))


class RunnerTests(unittest.TestCase):

    def test_set_node_autowarp_writes_only_the_flag(self):
        c = mission_runner.KrpcMissionControl(use_mechjeb=True)
        ne = types.SimpleNamespace(autowarp=True)
        c._mechjeb = types.SimpleNamespace(node_executor=ne)
        vessel = types.SimpleNamespace(control=types.SimpleNamespace())
        c._conn = types.SimpleNamespace(
            space_center=types.SimpleNamespace(active_vessel=vessel))
        c.perform(Action(mlib.ACTION_MJ_SET_NODE_AUTOWARP, 0.0))
        self.assertIs(False, ne.autowarp)
        c.perform(Action(mlib.ACTION_MJ_SET_NODE_AUTOWARP, 1.0))
        self.assertIs(True, ne.autowarp)

    def test_closest_approach_read(self):
        c = mission_runner.KrpcMissionControl(use_mechjeb=True)
        tgt_orbit = object()

        class _Orbit:
            def time_of_closest_approach(self, o):
                assert o is tgt_orbit
                return 8603.0

            def distance_at_closest_approach(self, o):
                return 98.5

        vessel = types.SimpleNamespace(orbit=_Orbit())
        sc = types.SimpleNamespace(target_vessel=types.SimpleNamespace(orbit=tgt_orbit))
        self.assertEqual((8603.0, 98.5), c._read_target_closest_approach(sc, vessel))
        none_ut, none_d = c._read_target_closest_approach(
            types.SimpleNamespace(target_vessel=None), vessel)
        self.assertTrue(math.isnan(none_ut) and math.isnan(none_d))


if __name__ == "__main__":
    unittest.main()
