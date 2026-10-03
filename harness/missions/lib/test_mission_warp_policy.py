"""Unit tests for the MISSION WARP POLICY (operator ruling 2026-10-01).

Burns stay at 1x; the idle 1x stretches around them are warped:
  - capture-node waits (rails, a HELD executor hand-off),
  - the vacuum landing coast (rails),
  - physics dwells: the PARK dwell and the kx COAST wait,
  - B4's REENTRY exo coast hopping while still ascending.
B4's deorbit slew is deliberately NOT warped (an attitude slew, not a coast).

Runnable with the stdlib runner only::

    cd harness && python -m unittest discover -s missions/lib -q

Every machine fixture below starts from the pre-policy test params in
test_mlib (which pin the policy OFF) and flips ONLY the switch the cell is
about, so a cell fails for the reason it names.
"""

import math
import os
import tomllib
import unittest
from dataclasses import replace

import mlib
from mlib import Action
from test_mlib import (B4_PARAMS, B11_PARAMS, B13_PARAMS, B15_TRIM_PARAMS,
                       _b4_state, _parked, _descending, snap)


def kinds(actions):
    return [a.kind for a in actions]


# Readable warp-policy inputs (the B11 Mun-capture shape): 250 kN on a
# 10 t stage -> 25 m/s^2, so a 277 m/s capture node is a ~5.5 s half burn.
POLICY_INPUTS = dict(vessel_mass=10_000.0, available_thrust=250_000.0,
                     atmosphere_depth=0.0, surface_gravity=1.63)


class PurePlanTests(unittest.TestCase):

    def test_half_burn_is_the_linear_upper_bound(self):
        self.assertAlmostEqual(5.54, mlib.half_burn_seconds(277.0, 250_000.0, 10_000.0),
                               places=2)
        for bad in ((float("nan"), 1.0, 1.0), (1.0, 0.0, 1.0), (1.0, 1.0, float("nan"))):
            self.assertTrue(math.isnan(mlib.half_burn_seconds(*bad)), bad)

    def test_node_wait_lands_lead_plus_half_burn_before_the_node(self):
        target, why = mlib.node_wait_warp_plan(
            1000.0, 6000.0, 277.0, 250_000.0, 10_000.0, float("nan"),
            200_000.0, 130_000.0, 0.0)
        self.assertAlmostEqual(6000.0 - 5.54 - mlib.NODE_WAIT_ORIENT_LEAD_SECONDS,
                               target, places=1)
        self.assertIn("node-wait", why)

    def test_node_wait_fails_closed_on_unread_mass_or_atmosphere(self):
        self.assertIsNone(mlib.node_wait_warp_plan(
            1000.0, 6000.0, 277.0, 250_000.0, float("nan"), float("nan"),
            200_000.0, 130_000.0, 0.0)[0])
        self.assertIsNone(mlib.node_wait_warp_plan(
            1000.0, 6000.0, 277.0, 250_000.0, 10_000.0, float("nan"),
            200_000.0, 130_000.0, float("nan"))[0])

    def test_node_wait_refuses_a_periapsis_inside_the_atmosphere(self):
        target, why = mlib.node_wait_warp_plan(
            1000.0, 6000.0, 100.0, 1.0e6, 10_000.0, float("nan"),
            90_000.0, 60_000.0, 70_000.0)
        self.assertIsNone(target)
        self.assertIn("periapsis", why)

    def test_node_wait_lands_before_a_soi_change(self):
        target, why = mlib.node_wait_warp_plan(
            1000.0, 9000.0, 100.0, 1.0e6, 10_000.0, 3000.0,
            200_000.0, 130_000.0, 0.0)
        self.assertAlmostEqual(1000.0 + 3000.0 - mlib.NODE_WAIT_SOI_MARGIN_SECONDS, target)
        self.assertIn("soi-clamped", why)

    def test_node_wait_skips_a_short_window(self):
        target, why = mlib.node_wait_warp_plan(
            1000.0, 1000.0 + mlib.NODE_WAIT_ORIENT_LEAD_SECONDS + 30.0, 10.0,
            1.0e6, 10_000.0, float("nan"), 200_000.0, 130_000.0, 0.0)
        self.assertIsNone(target)
        self.assertIn("window", why)

    def test_descent_floor_is_above_the_approach_altitude(self):
        self.assertEqual(37_500.0, mlib.descent_warp_floor("Mun"))
        self.assertEqual(22_000.0, mlib.descent_warp_floor("Minmus"))
        self.assertIsNone(mlib.descent_warp_floor("Kraken"))
        for body, limits in mlib.STOCK_WARP_ALTITUDE_LIMITS.items():
            self.assertGreater(mlib.descent_warp_floor(body), limits[4], body)

    def test_descent_plan_arrives_early_under_surface_gravity(self):
        target, why = mlib.descent_coast_warp_plan(
            100.0, 120_000.0, -50.0, "Mun", 0.0, 1.63, mlib.COAST_STATIC_FRAMES)
        drop = 120_000.0 - 37_500.0
        fall = (-50.0 + math.sqrt(50.0 ** 2 + 2 * 1.63 * drop)) / 1.63
        self.assertAlmostEqual(100.0 + fall * mlib.DESCENT_WARP_TIME_FRACTION, target)
        self.assertIn("descent-coast", why)

    def test_descent_plan_refusals(self):
        args = dict(now_ut=100.0, altitude=120_000.0, vertical_speed=-50.0,
                    body="Mun", atmosphere_depth=0.0, surface_gravity=1.63,
                    static_frames=mlib.COAST_STATIC_FRAMES)
        for over, word in ((dict(atmosphere_depth=70_000.0), "atmospheric"),
                           (dict(atmosphere_depth=float("nan")), "unread"),
                           (dict(static_frames=1), "static"),
                           (dict(altitude=30_000.0), "floor"),
                           (dict(altitude=40_000.0), "window"),
                           (dict(surface_gravity=float("nan")), "unread"),
                           (dict(body="Kraken"), "table")):
            target, why = mlib.descent_coast_warp_plan(**{**args, **over})
            self.assertIsNone(target, over)
            self.assertIn(word, why, over)

    def test_physics_dwell_index_drops_in_the_tail(self):
        self.assertEqual(mlib.PHYSICS_DWELL_WARP_INDEX,
                         mlib.physics_dwell_warp_index(mlib.PHYSICS_DWELL_TAIL_SECONDS + 1))
        self.assertEqual(0, mlib.physics_dwell_warp_index(mlib.PHYSICS_DWELL_TAIL_SECONDS))
        self.assertEqual(0, mlib.physics_dwell_warp_index(float("nan")))

    def test_orbit_static_needs_both_apsides_and_a_previous_frame(self):
        self.assertTrue(mlib.orbit_static(1000.0, 500.0, 1003.0, 498.0))
        self.assertFalse(mlib.orbit_static(1000.0, 500.0, 1010.0, 500.0))
        self.assertFalse(mlib.orbit_static(None, 500.0, 1000.0, 500.0))
        self.assertFalse(mlib.orbit_static(1000.0, 500.0, float("nan"), 500.0))


def _plan_capture_state():
    """A capture lane in PLAN-CAPTURE with the capture planned (node on board)."""
    base = mlib.b5_initial_state(B11_PARAMS)
    return replace(base, phase=mlib.B5_PLAN_CAPTURE, phase_entry_ut=16000.0,
                   phases_reached=base.phases_reached + (mlib.B5_PLAN_CAPTURE,),
                   capture_arm_streak=0)


def _capture_frame(**kw):
    base = dict(ut=16466.0, body="Mun", situation="ESCAPING", altitude=2_200_000.0,
                apoapsis=-1_562_246.0, periapsis=138_828.0, eccentricity=1.2,
                node_count=1, node_dv=277.0, node_ut=21568.0,
                time_to_periapsis=5102.0, node_executor_enabled=0)
    base.update(POLICY_INPUTS)
    base.update(kw)
    return snap(**base)


class NodeWaitHoldTests(unittest.TestCase):
    """The held executor hand-off, driven through the real b5_decide."""

    def _enter_burn(self):
        state, actions = mlib._b5_plan_phase(
            _plan_capture_state(), _capture_frame(), None,
            plan_action=None, burn_phase=mlib.B5_CAPTURE_BURN, on_timeout_phase=None,
            handoff_action=Action(mlib.ACTION_MJ_EXECUTE_NODES))
        return state, actions

    def test_a_far_node_is_held_and_warped_toward_not_handed_off(self):
        state, actions = self._enter_burn()
        self.assertEqual(mlib.B5_CAPTURE_BURN, state.phase)
        self.assertEqual([mlib.ACTION_WARP_TO_UT], kinds(actions))
        expected = 21568.0 - 277.0 * 10_000.0 / (2 * 250_000.0) \
            - mlib.NODE_WAIT_ORIENT_LEAD_SECONDS
        self.assertAlmostEqual(expected, actions[0].value, places=3)
        self.assertIn("node-wait", actions[0].text)
        self.assertEqual(actions[0].value, state.node_wait_ut)
        self.assertEqual(actions[0].value, state.warp_to_cmd)
        self.assertEqual(mlib.ACTION_MJ_EXECUTE_NODES, state.node_wait_handoff)

    def test_the_hold_owns_the_frame_so_no_executor_reissue_fires(self):
        """MUTATION: drop the b5_decide intercept and the disabled executor
        reads as dead -> mj_execute_nodes re-issues mid-warp."""
        state, _ = self._enter_burn()
        for i in range(mlib.CAPTURE_EXECUTOR_DISABLED_DEBOUNCE_FRAMES + 3):
            state, actions = mlib.b5_decide(state, _capture_frame(
                ut=17000.0 + 100.0 * i, warping_to=state.node_wait_ut,
                warp_mode="RAILS", warp_rate=1000.0))
            self.assertNotIn(mlib.ACTION_MJ_EXECUTE_NODES, kinds(actions))
            self.assertFalse(state.done)
        self.assertEqual(0, state.capture_exec_reissues)

    def test_arrival_releases_the_owed_handoff_with_its_reason(self):
        state, _ = self._enter_burn()
        state, actions = mlib.b5_decide(state, _capture_frame(
            ut=state.node_wait_ut - 1.0, situation="ESCAPING"))
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], kinds(actions))
        self.assertIn("released: arrived", actions[0].text)
        self.assertIsNone(state.node_wait_ut)
        self.assertIsNone(state.warp_to_cmd)
        self.assertIsNone(state.burn_static_since)

    def test_release_cancels_a_warp_still_running(self):
        state, _ = self._enter_burn()
        state, actions = mlib.b5_decide(state, _capture_frame(
            ut=state.node_wait_ut, warping_to=state.node_wait_ut))
        self.assertEqual([mlib.ACTION_CANCEL_WARP, mlib.ACTION_MJ_EXECUTE_NODES],
                         kinds(actions))

    def test_a_vanished_node_releases_without_a_handoff(self):
        state, _ = self._enter_burn()
        state, actions = mlib.b5_decide(state, _capture_frame(ut=17000.0, node_count=0))
        self.assertNotIn(mlib.ACTION_MJ_EXECUTE_NODES, kinds(actions))
        self.assertIsNone(state.node_wait_ut)

    def test_a_dead_warp_self_heals_then_gives_up_to_the_executor(self):
        state, _ = self._enter_burn()
        issued = state.phase_warp_issues
        ut = 17000.0
        reissued = 0
        while state.node_wait_ut is not None and ut < 21000.0:
            ut += mlib.WARP_REISSUE_SECONDS
            state, actions = mlib.b5_decide(state, _capture_frame(ut=ut))
            reissued += kinds(actions).count(mlib.ACTION_WARP_TO_UT)
        self.assertEqual(mlib.NODE_WAIT_MAX_ISSUES - issued, reissued)
        self.assertIn(mlib.ACTION_MJ_EXECUTE_NODES, kinds(actions))
        self.assertIn("warp not taking", actions[-1].text)

    def test_unread_mass_keeps_the_pre_policy_handoff_byte_for_byte(self):
        state, actions = mlib._b5_plan_phase(
            _plan_capture_state(), _capture_frame(vessel_mass=float("nan")), None,
            plan_action=None, burn_phase=mlib.B5_CAPTURE_BURN, on_timeout_phase=None,
            handoff_action=Action(mlib.ACTION_MJ_EXECUTE_NODES))
        self.assertEqual([Action(mlib.ACTION_MJ_EXECUTE_NODES)], actions)
        self.assertIsNone(state.node_wait_ut)

    def test_a_readable_refusal_rides_the_handoff_text(self):
        state, actions = mlib._b5_plan_phase(
            _plan_capture_state(), _capture_frame(node_ut=16466.0 + 150.0), None,
            plan_action=None, burn_phase=mlib.B5_CAPTURE_BURN, on_timeout_phase=None,
            handoff_action=Action(mlib.ACTION_MJ_EXECUTE_NODES))
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], kinds(actions))
        self.assertIn("node-wait declined", actions[0].text)
        self.assertIsNone(state.node_wait_ut)

    def test_the_diy_burner_handoff_is_never_held(self):
        state, actions = mlib._b5_plan_phase(
            _plan_capture_state(), _capture_frame(), None,
            plan_action=None, burn_phase=mlib.B5_CORRECTION_BURN,
            on_timeout_phase=mlib.B5_COAST_TO_TARGET,
            handoff_action=Action(mlib.ACTION_AP_POINT_NODE))
        self.assertEqual([Action(mlib.ACTION_AP_POINT_NODE)], actions)
        self.assertIsNone(state.node_wait_ut)

    def test_a_transfer_node_is_never_held(self):
        """Only CAPTURE-BURN holds: a held TLI ended 3.7 s earlier on all three
        Mun verification flights and re-timed the arrival. MUTATION: add
        B5_TRANSFER_BURN back to _B5_NODE_WAIT_PHASES and this reds."""
        base = replace(_plan_capture_state(), phase=mlib.B5_PLAN_TRANSFER)
        state, actions = mlib._b5_plan_phase(
            base, _capture_frame(body="Kerbin"), None, plan_action=None,
            burn_phase=mlib.B5_TRANSFER_BURN, on_timeout_phase=None,
            handoff_action=Action(mlib.ACTION_MJ_EXECUTE_NODES))
        self.assertEqual(mlib.B5_TRANSFER_BURN, state.phase)
        self.assertEqual([Action(mlib.ACTION_MJ_EXECUTE_NODES)], actions)
        self.assertIsNone(state.node_wait_ut)

    def test_the_switch_turns_the_hold_off(self):
        base = replace(_plan_capture_state(),
                       params=replace(B11_PARAMS, node_wait_warp=False))
        state, actions = mlib._b5_plan_phase(
            base, _capture_frame(), None, plan_action=None,
            burn_phase=mlib.B5_CAPTURE_BURN, on_timeout_phase=None,
            handoff_action=Action(mlib.ACTION_MJ_EXECUTE_NODES))
        self.assertEqual([Action(mlib.ACTION_MJ_EXECUTE_NODES)], actions)


# B22's park round-out, 2026-10-03_1106: the trim node issued at ut 1716.317
# with nodeUt 2687.196 and nodeDv 67.745 on the 650 kN stack, park 702 km x
# 560 km over Kerbin. The mass is a round 100 t, so the half burn is
# 67.745 * 100000 / (2 * 650000) = 5.211 s.
CIRC_NODE_UT = 2687.196
CIRC_ENTRY_UT = 1715.257
CIRC_HALF_BURN = 67.745 * 100_000.0 / (2.0 * 650_000.0)
CIRC_TARGET = CIRC_NODE_UT - CIRC_HALF_BURN - mlib.NODE_WAIT_ORIENT_LEAD_SECONDS


def _circ_state(armed=True, **over):
    """CIRCULARIZE with one park-trim plan issued and not yet handed over."""
    params = replace(B15_TRIM_PARAMS, circularize_node_wait_warp=armed)
    base = mlib.b5_initial_state(params)
    fields = {**base.__dict__, "phase": mlib.B5_CIRCULARIZE,
              "phase_entry_ut": CIRC_ENTRY_UT, "park_trim_attempts": 1}
    fields.update(over)
    return base.__class__(**fields)


def _circ_frame(**kw):
    base = dict(ut=1716.317, body="Kerbin", situation="ORBITING",
                altitude=702_284.0, apoapsis=769_634.457, periapsis=560_640.170,
                eccentricity=0.0826, node_count=1, node_dv=67.745,
                node_ut=CIRC_NODE_UT, vessel_mass=100_000.0,
                available_thrust=650_000.0, atmosphere_depth=70_000.0,
                node_executor_enabled=0)
    base.update(kw)
    return snap(**base)


class CircularizeNodeWaitTests(unittest.TestCase):
    """circularizeNodeWaitWarp: the CAPTURE node-wait hold, extended to the
    CIRCULARIZE park round-out node, driven through the real b5_decide."""

    def _held(self):
        state, actions = mlib.b5_decide(_circ_state(), _circ_frame())
        self.assertEqual([mlib.ACTION_WARP_TO_UT], kinds(actions))
        return state, actions

    def test_the_key_arms_the_hold_on_the_park_trim_handoff(self):
        state, actions = self._held()
        self.assertEqual(mlib.B5_CIRCULARIZE, state.phase)
        self.assertAlmostEqual(CIRC_TARGET, actions[0].value, places=6)
        self.assertEqual(actions[0].value, state.node_wait_ut)
        self.assertEqual(actions[0].value, state.warp_to_cmd)
        self.assertEqual(mlib.ACTION_MJ_EXECUTE_NODES, state.node_wait_handoff)
        # The hand-off is owed, so the trim counts it as made: no later frame
        # may re-plan or re-execute behind the hold.
        self.assertEqual(1, state.park_trim_execs)
        # The analyzer's halfBurn= token rides the action text.
        self.assertIn("node-wait: nodeUt=2687.2 halfBurn=5.2 lead=120",
                      actions[0].text)

    def test_default_off_is_the_old_handoff_byte_for_byte(self):
        """MUTATION: drop the circularize_node_wait_warp test in
        _b5_node_wait_phases and the bare hand-off becomes a warp."""
        before = _circ_state(armed=False)
        frame = _circ_frame()
        state, actions = mlib.b5_decide(before, frame)
        self.assertEqual([Action(mlib.ACTION_MJ_EXECUTE_NODES)], actions)
        # The pre-change line: replace(stayed, park_trim_execs=execs + 1).
        # (b5_decide's frozen-telemetry signature is stamped before the phase.)
        stayed = mlib._b5_stay_or_flake(before, frame, frame.apoapsis)
        self.assertEqual(replace(stayed, park_trim_execs=1,
                                 frozen_sig=state.frozen_sig,
                                 frozen_count=state.frozen_count), state)
        self.assertFalse(B15_TRIM_PARAMS.circularize_node_wait_warp)
        self.assertFalse(mlib.B5Params.__dataclass_fields__[
            "circularize_node_wait_warp"].default)
        self.assertEqual(mlib._B5_NODE_WAIT_PHASES,
                         mlib._b5_node_wait_phases(B15_TRIM_PARAMS))

    def test_the_spec_key_parses_and_defaults_off(self):
        self.assertFalse(mlib.b5_params_from_dict({}).circularize_node_wait_warp)
        self.assertTrue(mlib.b5_params_from_dict(
            {"circularizeNodeWaitWarp": True}).circularize_node_wait_warp)

    def test_only_b22_opts_in(self):
        here = os.path.dirname(os.path.abspath(__file__))
        scen = os.path.join(os.path.dirname(os.path.dirname(here)), "scenarios")
        armed = []
        for name in sorted(os.listdir(scen)):
            if not name.endswith(".toml"):
                continue
            with open(os.path.join(scen, name), "rb") as fh:
                spec = tomllib.load(fh)
            mp = spec.get("driver", {}).get("missionParams", {})
            if "circularizeNodeWaitWarp" in mp:
                armed.append((name, mp["circularizeNodeWaitWarp"]))
        self.assertEqual([("B22-jool-orbit.toml", True)], armed)

    def test_the_hold_never_covers_a_transfer_node(self):
        """Owner ruling: a held TLI moved B13's landing site. The key adds
        CIRCULARIZE only; MUTATION: add B5_TRANSFER_BURN to the opt-in."""
        self.assertEqual((mlib.B5_CAPTURE_BURN, mlib.B5_CIRCULARIZE),
                         mlib._b5_node_wait_phases(
                             replace(B15_TRIM_PARAMS, circularize_node_wait_warp=True)))
        base = replace(_circ_state(), phase=mlib.B5_PLAN_TRANSFER)
        state, actions = mlib._b5_plan_phase(
            base, _circ_frame(), None, plan_action=None,
            burn_phase=mlib.B5_TRANSFER_BURN, on_timeout_phase=None,
            handoff_action=Action(mlib.ACTION_MJ_EXECUTE_NODES))
        self.assertEqual(mlib.B5_TRANSFER_BURN, state.phase)
        self.assertEqual([Action(mlib.ACTION_MJ_EXECUTE_NODES)], actions)
        self.assertIsNone(state.node_wait_ut)

    def test_the_hold_owns_the_frame_and_never_writes_a_second_warp(self):
        state, _ = self._held()
        for i in range(6):
            state, actions = mlib.b5_decide(state, _circ_frame(
                ut=1800.0 + 100.0 * i, warping_to=CIRC_TARGET,
                warp_mode="RAILS", warp_rate=100.0))
            self.assertEqual([], actions)
            self.assertEqual(1, state.park_trim_execs)
            self.assertEqual(1, state.park_trim_attempts)

    def test_lead_boundary(self):
        # Release exactly NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS before the target.
        state, _ = self._held()
        edge = CIRC_TARGET - mlib.NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS
        early, actions = mlib.b5_decide(state, _circ_frame(
            ut=edge - 0.01, warping_to=CIRC_TARGET))
        self.assertEqual([], actions)
        self.assertIsNotNone(early.node_wait_ut)
        done, actions = mlib.b5_decide(state, _circ_frame(ut=edge))
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], kinds(actions))
        self.assertIn("released: arrived", actions[0].text)
        self.assertIsNone(done.node_wait_ut)
        self.assertIsNone(done.warp_to_cmd)
        # The released executor still has the whole orient lead (+ half burn
        # + tolerance) before the node, so no second of the burn is warped.
        self.assertAlmostEqual(
            mlib.NODE_WAIT_ORIENT_LEAD_SECONDS + CIRC_HALF_BURN
            + mlib.NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS, CIRC_NODE_UT - edge, places=6)
        # The plan's own window floor: exactly NODE_WAIT_MIN_WARP_SECONDS holds,
        # a hair less declines to the executor's own warp.
        now = CIRC_TARGET - mlib.NODE_WAIT_MIN_WARP_SECONDS
        _held, actions = mlib.b5_decide(_circ_state(), _circ_frame(ut=now))
        self.assertEqual([mlib.ACTION_WARP_TO_UT], kinds(actions))
        short, actions = mlib.b5_decide(_circ_state(), _circ_frame(ut=now + 0.1))
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], kinds(actions))
        self.assertIn("node-wait declined: warp window", actions[0].text)
        self.assertIsNone(short.node_wait_ut)
        self.assertEqual(1, short.park_trim_execs)

    def test_rails_legality_is_fail_closed(self):
        limit = mlib.STOCK_WARP_ALTITUDE_LIMITS["Mun"][1]
        self.assertEqual((True, ""), mlib.node_wait_rails_legal("Mun", 50_000.0, limit))
        self.assertFalse(mlib.node_wait_rails_legal("Mun", 50_000.0, limit - 1.0)[0])
        self.assertFalse(mlib.node_wait_rails_legal("Mun", limit - 1.0, 50_000.0)[0])
        self.assertFalse(mlib.node_wait_rails_legal("Mun", float("nan"), 50_000.0)[0])
        self.assertFalse(mlib.node_wait_rails_legal("Nowhere", 1.0e6, 1.0e6)[0])
        # B22's 702 x 560 km park is legal.
        self.assertTrue(mlib.node_wait_rails_legal("Kerbin", 702_284.0, 560_640.0)[0])

    def test_a_rails_illegal_park_declines_to_the_executor(self):
        """An airless body, so only the rails clamp can refuse (the plan's
        atmosphere check would mask it on Kerbin). MUTATION: drop the
        node_wait_rails_legal call and this arms a warp WarpTo would fly as
        PHYSICS warp."""
        # Below the Mun's factor-1 limit: the hand-off seam itself (the
        # CIRCULARIZE periapsis gate would never reach the trim this low).
        state, actions = mlib._b5_node_wait_begin(
            _circ_state(), _circ_frame(body="Mun", altitude=600_000.0,
                                       periapsis=4_000.0, atmosphere_depth=0.0),
            Action(mlib.ACTION_MJ_EXECUTE_NODES))
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], kinds(actions))
        self.assertIn("node-wait declined: rails-illegal", actions[0].text)
        self.assertIn("below the 5000 m rails limit", actions[0].text)
        self.assertIsNone(state.node_wait_ut)
        # A body outside the rails table, through the real b5_decide.
        state, actions = mlib.b5_decide(_circ_state(), _circ_frame(
            body="Nowhere", atmosphere_depth=0.0))
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], kinds(actions))
        self.assertIn("node-wait declined: rails-illegal no rails table",
                      actions[0].text)
        self.assertIsNone(state.node_wait_ut)
        self.assertEqual(1, state.park_trim_execs)

    def test_an_engaged_or_unread_executor_is_never_warped_over(self):
        """An enabled executor already owns the warp; a second writer is what
        the hold exists to avoid. -1 (unread) fails closed."""
        for value in (1, -1):
            state, actions = mlib.b5_decide(_circ_state(), _circ_frame(
                node_executor_enabled=value))
            self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], kinds(actions), value)
            self.assertIn("executor not observed idle (nodeExec=%d)" % value,
                          actions[0].text)
            self.assertIsNone(state.node_wait_ut)

    def test_a_spent_warp_budget_declines(self):
        state, actions = mlib.b5_decide(
            _circ_state(phase_warp_issues=mlib.NODE_WAIT_MAX_ISSUES), _circ_frame())
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], kinds(actions))
        self.assertIn("warp budget spent", actions[0].text)
        self.assertIsNone(state.node_wait_ut)

    def test_release_cancels_the_warp_before_the_executor_warps(self):
        """Cancel-before-rails: the release frame drops the machine's native
        warp BEFORE the autowarping executor is engaged, in that order, and
        writes no warp of its own. MUTATION: swap the two appends in
        _b5_node_wait_step and this reds."""
        state, _ = self._held()
        released, actions = mlib.b5_decide(state, _circ_frame(
            ut=CIRC_TARGET, warping_to=CIRC_TARGET))
        self.assertEqual([mlib.ACTION_CANCEL_WARP, mlib.ACTION_MJ_EXECUTE_NODES],
                         kinds(actions))
        self.assertNotIn(mlib.ACTION_WARP_TO_UT, kinds(actions))
        self.assertNotIn(mlib.ACTION_SET_RAILS_WARP, kinds(actions))
        self.assertIsNone(released.warp_to_cmd)
        self.assertEqual(0, released.warp_cmd)
        # The budget release (before the target) cancels first too.
        old = replace(state, phase_entry_ut=1800.0
                      - B15_TRIM_PARAMS.circularize_timeout - 1.0)
        _st, actions = mlib.b5_decide(old, _circ_frame(
            ut=1800.0, warping_to=CIRC_TARGET))
        self.assertEqual([mlib.ACTION_CANCEL_WARP, mlib.ACTION_MJ_EXECUTE_NODES],
                         kinds(actions))
        self.assertIn("phase budget expired", actions[-1].text)

    def test_a_vanished_node_releases_and_the_trim_reads_the_orbit(self):
        state, _ = self._held()
        state, actions = mlib.b5_decide(state, _circ_frame(ut=1800.0, node_count=0))
        self.assertNotIn(mlib.ACTION_MJ_EXECUTE_NODES, kinds(actions))
        self.assertIsNone(state.node_wait_ut)
        # Next frame: a round park leaves through the unchanged trim ladder.
        state, actions = mlib.b5_decide(state, _circ_frame(
            ut=1801.0, node_count=0, apoapsis=769_700.0, periapsis=769_500.0,
            eccentricity=0.0001))
        self.assertEqual(mlib.B5_ORBIT, state.phase)

    def test_a_held_circularize_that_dies_cancels_its_warp(self):
        state, _ = self._held()
        state, actions = mlib.b5_decide(state, snap(ut=1800.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(actions))


def _park_state(**over):
    base = mlib.b5_initial_state(replace(B11_PARAMS, park_physics_warp=True))
    fields = {**base.__dict__, "phase": mlib.B5_PARK, "phase_entry_ut": 0.0}
    fields.update(over)
    return base.__class__(**fields)


class ParkPhysicsDwellTests(unittest.TestCase):

    def test_an_in_gate_dwell_runs_under_physics_warp(self):
        state, actions = mlib.b5_decide(_park_state(), _parked(ut=10.0))
        self.assertEqual([(mlib.ACTION_SET_PHYSICS_WARP, float(mlib.PHYSICS_DWELL_WARP_INDEX))],
                         [(a.kind, a.value) for a in actions])
        self.assertIn("park dwell", actions[0].text)
        self.assertEqual(mlib.PHYSICS_DWELL_WARP_INDEX, state.phys_warp_cmd)
        # On-change only: the next in-gate frame emits nothing.
        state, actions = mlib.b5_decide(state, _parked(ut=11.0))
        self.assertEqual([], actions)

    def test_a_tumbling_frame_drops_to_1x(self):
        state = _park_state(phys_warp_cmd=mlib.PHYSICS_DWELL_WARP_INDEX)
        state, actions = mlib.b5_decide(state, _parked(ut=10.0, angular_velocity=0.5))
        self.assertEqual([(mlib.ACTION_SET_PHYSICS_WARP, 0.0)],
                         [(a.kind, a.value) for a in actions])

    def test_the_tail_is_1x_so_the_commit_frame_never_warps(self):
        dwell = B11_PARAMS.park_dwell
        state = _park_state(phys_warp_cmd=mlib.PHYSICS_DWELL_WARP_INDEX,
                            park_stable_streak=B11_PARAMS.park_debounce)
        state, actions = mlib.b5_decide(
            state, _parked(ut=dwell - mlib.PHYSICS_DWELL_TAIL_SECONDS + 1.0))
        self.assertEqual([(mlib.ACTION_SET_PHYSICS_WARP, 0.0)],
                         [(a.kind, a.value) for a in actions])
        state, actions = mlib.b5_decide(state, _parked(ut=dwell + 1.0))
        self.assertEqual([mlib.ACTION_PARSEK_COMMIT_TREE], kinds(actions))

    def test_an_exit_while_warped_is_torn_down_first(self):
        """The backstop: a commit can never start under physics warp."""
        state = _park_state(phys_warp_cmd=mlib.PHYSICS_DWELL_WARP_INDEX,
                            park_stable_streak=B11_PARAMS.park_debounce)
        state, actions = mlib.b5_decide(state, _parked(ut=B11_PARAMS.park_dwell + 1.0))
        self.assertEqual([mlib.ACTION_SET_PHYSICS_WARP, mlib.ACTION_PARSEK_COMMIT_TREE],
                         kinds(actions))
        self.assertEqual(0, state.phys_warp_cmd)


def _descent_state(**over):
    base = mlib.b5_initial_state(replace(B13_PARAMS, descent_coast_warp=True))
    fields = {**base.__dict__, "phase": mlib.B5_DESCENT, "phase_entry_ut": 0.0,
              "landing_alt_ref": 135_000.0, "landing_alt_ref_ut": 0.0}
    fields.update(over)
    return base.__class__(**fields)


def _coasting(**kw):
    base = dict(altitude=120_000.0, vertical_speed=-40.0, apoapsis=138_596.0,
                periapsis=-20_230.0, atmosphere_depth=0.0, surface_gravity=1.63)
    base.update(kw)
    return _descending(**base)


class DescentCoastWarpTests(unittest.TestCase):

    def test_a_static_impact_coast_warps_after_the_debounce(self):
        state = _descent_state()
        state, actions = mlib.b5_decide(state, _coasting(ut=10.0))
        self.assertEqual([], actions)          # first frame: no previous apsides
        state, actions = mlib.b5_decide(state, _coasting(ut=11.0))
        self.assertEqual([], actions)          # one static frame
        state, actions = mlib.b5_decide(state, _coasting(ut=12.0))
        self.assertEqual([mlib.ACTION_WARP_TO_UT], kinds(actions))
        self.assertIn("descent-coast", actions[0].text)
        self.assertEqual(actions[0].value, state.warp_to_cmd)

    def test_a_burning_frame_never_warps(self):
        state = _descent_state()
        for i, pe in enumerate((-10_000.0, -12_000.0, -14_000.0, -16_000.0)):
            state, actions = mlib.b5_decide(state, _coasting(ut=10.0 + i, periapsis=pe))
            self.assertEqual([], actions)

    def test_an_orbit_still_above_the_surface_never_warps(self):
        """The deorbit burn is still owed: MechJeb may be orienting for it."""
        state = _descent_state()
        for i in range(4):
            state, actions = mlib.b5_decide(state, _coasting(
                ut=10.0 + i, periapsis=130_000.0, vertical_speed=-1.0))
            self.assertEqual([], actions)

    def test_the_warp_is_cancelled_below_the_floor(self):
        state = _descent_state(warp_to_cmd=900.0, coast_prev_ap=138_596.0,
                               coast_prev_pe=-20_230.0,
                               coast_static_frames=mlib.COAST_STATIC_FRAMES)
        state, actions = mlib.b5_decide(state, _coasting(
            ut=500.0, altitude=30_000.0, warping_to=900.0, warp_mode="RAILS",
            warp_rate=100.0))
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(actions))
        self.assertIn("floor", actions[0].text)
        self.assertIsNone(state.warp_to_cmd)

    def test_an_atmospheric_body_never_warps(self):
        state = _descent_state()
        for i in range(4):
            state, actions = mlib.b5_decide(state, _coasting(
                ut=10.0 + i, atmosphere_depth=70_000.0))
            self.assertEqual([], actions)


class B4AscendingCoastHopTests(unittest.TestCase):
    """B4's REENTRY exo coast hops while still ascending after the cutoff (it
    polled at 1x until vertical speed went negative). The deorbit SLEW is NOT
    warped: it is an attitude slew, outside the ruling's coasts and node waits."""

    def _reentry(self, **over):
        return _b4_state(mlib.B4_REENTRY, phase_entry_ut=0.0, stages_owed=0, **over)

    def test_an_ascending_exo_coast_hops(self):
        state, actions = mlib.b4_decide(self._reentry(), snap(
            ut=700.0, altitude=83_700.0, vertical_speed=6.0,
            periapsis=30_000.0, apoapsis=83_900.0))
        self.assertEqual([(mlib.ACTION_WARP_TO, 700.0 + B4_PARAMS.warp_hop_seconds)],
                         [(a.kind, a.value) for a in actions])

    def test_the_switch_restores_the_descending_only_hop(self):
        state = self._reentry(params=replace(B4_PARAMS, ascending_coast_hops=False))
        _, actions = mlib.b4_decide(state, snap(
            ut=700.0, altitude=83_700.0, vertical_speed=6.0,
            periapsis=30_000.0, apoapsis=83_900.0))
        self.assertEqual([], actions)

    def test_never_below_the_hop_floor(self):
        _, actions = mlib.b4_decide(self._reentry(), snap(
            ut=700.0, altitude=B4_PARAMS.warp_above_alt - 1.0, vertical_speed=6.0,
            periapsis=30_000.0, apoapsis=83_900.0))
        self.assertEqual([], actions)

    def test_the_deorbit_slew_stays_at_1x(self):
        state = _b4_state(mlib.B4_DEORBIT, phase_entry_ut=0.0)
        _, actions = mlib.b4_decide(state, snap(
            ut=30.0, altitude=80_000.0, ap_error=150.0, atmosphere_depth=70_000.0,
            periapsis=75_000.0, apoapsis=84_000.0))
        self.assertEqual([], actions)


def _kx_coast(**over):
    params = mlib.kxrw_params_from_dict({"coastSeconds": 150.0})
    base = mlib.kxrw_initial_state(params)
    return replace(base, phase=mlib.KXRW_COAST, phase_entry_ut=100.0, **over)


class KxCoastPhysicsWarpTests(unittest.TestCase):

    def test_above_the_atmosphere_with_the_throttle_off_the_wait_warps(self):
        state, actions = mlib.kxrw_decide(_kx_coast(), snap(
            ut=120.0, altitude=75_000.0, throttle=0.0, atmosphere_depth=70_000.0,
            situation="SUB_ORBITAL"))
        self.assertEqual([(mlib.ACTION_SET_PHYSICS_WARP, float(mlib.PHYSICS_DWELL_WARP_INDEX))],
                         [(a.kind, a.value) for a in actions])
        self.assertEqual(mlib.KXRW_COAST, state.phase)

    def test_inside_the_atmosphere_or_under_thrust_it_stays_1x(self):
        for over in (dict(altitude=60_000.0), dict(throttle=0.5),
                     dict(atmosphere_depth=float("nan"))):
            frame = dict(ut=120.0, altitude=75_000.0, throttle=0.0,
                         atmosphere_depth=70_000.0, situation="SUB_ORBITAL")
            frame.update(over)
            _, actions = mlib.kxrw_decide(_kx_coast(), snap(**frame))
            self.assertEqual([], actions, over)

    def test_the_exit_frame_drops_physics_warp_before_leaving(self):
        state = _kx_coast(coast_phys_warp_cmd=mlib.PHYSICS_DWELL_WARP_INDEX)
        state, actions = mlib.kxrw_decide(state, snap(
            ut=260.0, altitude=150_000.0, throttle=0.0, atmosphere_depth=70_000.0))
        self.assertEqual([(mlib.ACTION_SET_PHYSICS_WARP, 0.0)],
                         [(a.kind, a.value) for a in actions])
        self.assertEqual(mlib.KXRW_COAST, state.phase)
        state, actions = mlib.kxrw_decide(state, snap(
            ut=261.0, altitude=150_000.0, throttle=0.0, atmosphere_depth=70_000.0))
        self.assertNotEqual(mlib.KXRW_COAST, state.phase)

    def test_the_shell_ceiling_is_4x_in_coast_only(self):
        self.assertEqual(4.0, mlib.kxrw_max_physics_warp(_kx_coast()))
        self.assertEqual(0.0, mlib.kxrw_max_physics_warp(
            replace(_kx_coast(), phase=mlib.KXRW_TREE_STATE)))


class TerminalTeardownTests(unittest.TestCase):
    """Every TERMINAL frame drops a warp the policy still holds, so the runner's
    cleanup tail never runs warped (review of #1958: the PARK exit through
    _b5_left_target_soi returned a terminal at 4x)."""

    def test_leaving_the_target_soi_in_park_drops_physics_warp(self):
        """MUTATION: remove the decorator from b5_decide and this reds."""
        state = _park_state(phys_warp_cmd=mlib.PHYSICS_DWELL_WARP_INDEX)
        state, actions = mlib.b5_decide(state, _parked(ut=10.0, body="Kerbin"))
        self.assertTrue(state.done)
        self.assertEqual([(mlib.ACTION_SET_PHYSICS_WARP, 0.0)],
                         [(a.kind, a.value) for a in actions])
        self.assertEqual(0, state.phys_warp_cmd)

    def test_a_lost_vessel_in_park_drops_physics_warp(self):
        state = _park_state(phys_warp_cmd=mlib.PHYSICS_DWELL_WARP_INDEX)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual([mlib.ACTION_SET_PHYSICS_WARP], kinds(actions))

    def test_the_park_give_up_is_not_torn_down_twice(self):
        state = _park_state(phys_warp_cmd=mlib.PHYSICS_DWELL_WARP_INDEX)
        state, actions = mlib.b5_decide(state, _parked(
            ut=B11_PARAMS.park_timeout + 1.0, angular_velocity=0.5))
        self.assertTrue(state.done)
        self.assertEqual(1, kinds(actions).count(mlib.ACTION_SET_PHYSICS_WARP))

    def test_a_lost_vessel_under_a_descent_warp_cancels_it(self):
        state = _descent_state(warp_to_cmd=900.0)
        state, actions = mlib.b5_decide(state, snap(ut=500.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(actions))
        self.assertIsNone(state.warp_to_cmd)

    def test_a_held_capture_that_dies_cancels_its_warp(self):
        base = mlib.b5_initial_state(B11_PARAMS)
        state = replace(base, phase=mlib.B5_CAPTURE_BURN, phase_entry_ut=0.0,
                        node_wait_ut=5000.0, warp_to_cmd=5000.0)
        state, actions = mlib.b5_decide(state, snap(ut=100.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(actions))

    def test_a_lost_vessel_in_the_kx_coast_drops_physics_warp(self):
        state = _kx_coast(coast_phys_warp_cmd=mlib.PHYSICS_DWELL_WARP_INDEX)
        state, actions = mlib.kxrw_decide(state, snap(ut=150.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual([(mlib.ACTION_SET_PHYSICS_WARP, 0.0)],
                         [(a.kind, a.value) for a in actions])

    def test_an_unwarped_terminal_emits_nothing_extra(self):
        state, actions = mlib.b5_decide(_park_state(), snap(ut=10.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual([], actions)


if __name__ == "__main__":
    unittest.main()
