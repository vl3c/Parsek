"""Unit tests for the b17_duna_direct lane: the stock heliocentric ephemeris,
the PAD-ALIGN phase of the shared B5 machine, the ASAP-ejection plan plumbing,
the missed-window guard, the padAlignedDirectEjection assertion row, and the
DD1 craft drift gate.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

Every cell names the regression it guards. The lane's two cleanliness
constraints (no parking-orbit loiter, no Ike encounter) are the "Looped re-aim
interplanetary transfer" todo entry's option-3 precondition (a); the cells
here pin the machinery that delivers the FIRST constraint structurally.
"""

import importlib.util
import math
import os
import unittest
from dataclasses import replace

import mlib
from mlib import Action, TelemetrySnapshot

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))


def snap(**kw):
    return TelemetrySnapshot(**kw)


def b17_params(**overrides):
    """A minimal B17-shaped missionParams dict (pad-align + interplanetary +
    capture), overridable per cell."""
    base = {
        "targetApoapsisMeters": 100000,
        "targetPeriapsisMeters": 100000,
        "apoErrorMeters": 5000,
        "periErrorMeters": 5000,
        "ascentTimeoutSeconds": 600,
        "circularizeTimeoutSeconds": 600,
        "parkTrimEccMax": 0.02,
        "targetBodyName": "Duna",
        "homeBodyName": "Kerbin",
        "interplanetaryTransfer": True,
        "viaBodyNames": ["Sun"],
        "returnBodyName": "Sun",
        "ejectionEccFloor": 1.05,
        "correctionTriggerTimeToSoiSeconds": [20_000_000, 500_000],
        "maxCorrectionDvMps": 450,
        "padAlignEjection": True,
        "padAlignMarginSeconds": 1500,
        "padAlignTimeoutSeconds": 300,
        "padAlignWindowGuardSeconds": 21600,
        "captureEnabled": True,
        "parkMinPeriapsisMeters": 60000,
        "parkMaxApoapsisMeters": 1_500_000,
        "parkMaxEccentricity": 0.2,
    }
    base.update(overrides)
    return base


class StockEphemerisTests(unittest.TestCase):
    """The committed heliocentric elements and the pure window math. The
    constants' authority is end-to-end (a wrong one lands the jump
    off-window and the window guard names it live), but these cells pin the
    DERIVED quantities against independently-known stock values so a typo'd
    constant reds headlessly first."""

    def test_kerbin_and_duna_periods_match_the_known_stock_values(self):
        kerbin = 2 * math.pi / mlib.heliocentric_mean_motion_rad_s("Kerbin")
        duna = 2 * math.pi / mlib.heliocentric_mean_motion_rad_s("Duna")
        self.assertAlmostEqual(kerbin, 9_203_544.6, delta=1.0)
        self.assertAlmostEqual(duna, 17_315_400.1, delta=1.0)

    def test_the_classical_kerbin_duna_phase_angle_is_the_community_44_36(self):
        self.assertAlmostEqual(
            mlib.classical_hohmann_phase_angle_deg("Kerbin", "Duna"),
            44.36, delta=0.01)

    def test_the_kerbin_eve_phase_angle_is_negative_inner_transfer(self):
        # Eve is an INNER planet: the departure phase angle is negative
        # (Kerbin leads). A sign error here would send the pad jump to a
        # moment the target trails the wrong way around.
        self.assertLess(
            mlib.classical_hohmann_phase_angle_deg("Kerbin", "Eve"), 0.0)

    def test_the_solved_window_actually_carries_the_ideal_phase_angle(self):
        window = mlib.next_ejection_window_ut("Kerbin", "Duna", 0.0)
        ideal = mlib.classical_hohmann_phase_angle_deg("Kerbin", "Duna")
        at_window = mlib.interplanetary_phase_angle_deg(
            "Kerbin", "Duna", window)
        self.assertGreater(window, 0.0)
        self.assertAlmostEqual(at_window, ideal, delta=1e-4)

    def test_the_window_is_at_or_after_from_ut_and_repeats_synodically(self):
        first = mlib.next_ejection_window_ut("Kerbin", "Duna", 0.0)
        second = mlib.next_ejection_window_ut("Kerbin", "Duna", first + 60.0)
        self.assertGreaterEqual(first, 0.0)
        self.assertGreater(second, first)
        synodic = 2 * math.pi / abs(
            mlib.heliocentric_mean_motion_rad_s("Kerbin")
            - mlib.heliocentric_mean_motion_rad_s("Duna"))
        # Duna's 0.051 eccentricity makes true-longitude crossings wobble
        # around the mean synodic spacing; 2 percent bounds the wobble with
        # margin while still catching a wrong-period constant outright.
        self.assertAlmostEqual(second - first, synodic,
                               delta=0.02 * synodic)

    def test_an_unknown_body_raises_with_the_known_list(self):
        with self.assertRaises(ValueError):
            mlib.next_ejection_window_ut("Kerbin", "Jool", 0.0)


class PadAlignParamsTests(unittest.TestCase):
    def test_pad_align_requires_interplanetary_transfer(self):
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(
                b17_params(interplanetaryTransfer=False))

    def test_pad_align_requires_committed_elements_for_both_bodies(self):
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(b17_params(targetBodyName="Jool"))

    def test_the_four_keys_parse_and_default_off(self):
        on = mlib.b5_params_from_dict(b17_params(
            padAlignMarginSeconds=1234, padAlignTimeoutSeconds=222,
            padAlignWindowGuardSeconds=4321))
        self.assertTrue(on.pad_align_ejection)
        self.assertEqual(on.pad_align_margin, 1234.0)
        self.assertEqual(on.pad_align_timeout, 222.0)
        self.assertEqual(on.pad_align_window_guard, 4321.0)
        off = mlib.b5_params_from_dict(b17_params(padAlignEjection=False))
        self.assertFalse(off.pad_align_ejection)


class PadAlignMachineTests(unittest.TestCase):
    """The PAD-ALIGN branch of b5_decide: one forward-only seam TimeJump on
    the pad, completion judged by UT ARRIVAL (never by the game-time budget,
    which the jump itself would trip), fail-closed tag-gated seam results."""

    def setUp(self):
        self.params = mlib.b5_params_from_dict(b17_params())
        self.state = mlib.b5_initial_state(self.params)

    def test_prelaunch_far_from_window_issues_one_seam_timejump(self):
        state, actions = mlib.b5_decide(self.state, snap(ut=1000.0))
        self.assertEqual(state.phase, mlib.B5_PAD_ALIGN)
        self.assertTrue(state.pad_align_jump_issued)
        self.assertEqual(len(actions), 1)
        act = actions[0]
        self.assertEqual(act.kind, mlib.ACTION_PARSEK_SEAM_COMMAND)
        self.assertEqual(act.seam_verb, "TimeJump")
        self.assertEqual(act.seam_tag, mlib.B5_TAG_PAD_ALIGN)
        args = dict(act.seam_args)
        window = mlib.next_ejection_window_ut("Kerbin", "Duna", 1000.0)
        self.assertAlmostEqual(float(args["ut"]),
                               window - self.params.pad_align_margin,
                               delta=0.01)
        self.assertAlmostEqual(state.pad_align_window_ut, window, delta=0.01)

    def test_prelaunch_already_inside_the_margin_launches_without_a_jump(self):
        window = mlib.next_ejection_window_ut("Kerbin", "Duna", 1000.0)
        at_window = snap(ut=window - 10.0)
        state, actions = mlib.b5_decide(self.state, at_window)
        self.assertEqual(state.phase, mlib.B5_MJ_ASCENT)
        self.assertEqual(state.launch_ut, at_window.ut)
        kinds = [a.kind for a in actions]
        self.assertIn(mlib.ACTION_MJ_ENGAGE_ASCENT, kinds)
        self.assertNotIn(mlib.ACTION_PARSEK_SEAM_COMMAND, kinds)

    def test_arrival_at_the_jump_target_launches_and_ignores_the_budget(self):
        armed, _ = mlib.b5_decide(self.state, snap(ut=1000.0))
        target = armed.pad_align_target_ut
        # The arrival frame's UT is ~4.6M seconds past phase entry -- far
        # beyond the 300 s phase budget. Completion must win over budget.
        state, actions = mlib.b5_decide(armed, snap(ut=target + 5.0))
        self.assertEqual(state.phase, mlib.B5_MJ_ASCENT)
        self.assertIsNone(state.verdict)
        self.assertEqual(state.launch_ut, target + 5.0)
        self.assertIn(mlib.ACTION_MJ_ENGAGE_ASCENT,
                      [a.kind for a in actions])

    def test_a_terminal_seam_error_flakes_with_the_decoded_reason(self):
        armed, _ = mlib.b5_decide(self.state, snap(ut=1000.0))
        state, _ = mlib.b5_decide(armed, snap(
            ut=1002.0, seam_command_result="REJECTED",
            seam_command_tag=mlib.B5_TAG_PAD_ALIGN,
            seam_command_payload=(("msg", "backward-jump"),)))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertIn("pad-align TimeJump", state.flake_reason)
        self.assertIn("REJECTED", state.flake_reason)
        self.assertIn("backward-jump", state.flake_reason)

    def test_a_stale_result_for_a_different_tag_is_ignored(self):
        armed, _ = mlib.b5_decide(self.state, snap(ut=1000.0))
        state, actions = mlib.b5_decide(armed, snap(
            ut=1002.0, seam_command_result="ERROR",
            seam_command_tag="someothertag"))
        self.assertFalse(state.done)
        self.assertEqual(state.phase, mlib.B5_PAD_ALIGN)
        self.assertEqual(actions, [])

    def test_flag_off_prelaunch_is_the_proven_immediate_launch(self):
        params = mlib.b5_params_from_dict(b17_params(padAlignEjection=False))
        state, actions = mlib.b5_decide(
            mlib.b5_initial_state(params), snap(ut=1000.0))
        self.assertEqual(state.phase, mlib.B5_MJ_ASCENT)
        self.assertEqual([a.kind for a in actions], [
            mlib.ACTION_MJ_SET_TARGET_APOAPSIS, mlib.ACTION_MJ_ENABLE_AUTOSTAGE,
            mlib.ACTION_MJ_ENGAGE_ASCENT, mlib.ACTION_ACTIVATE_STAGE])


class AsapPlanPlumbingTests(unittest.TestCase):
    def test_pad_align_plans_carry_the_asap_selector_value(self):
        params = mlib.b5_params_from_dict(b17_params())
        act = mlib._b5_transfer_plan_action(params)
        self.assertEqual(act.kind, mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER)
        self.assertEqual(act.value, 0.0)

    def test_b7_shaped_plans_still_carry_no_value(self):
        params = mlib.b5_params_from_dict(b17_params(
            padAlignEjection=False))
        act = mlib._b5_transfer_plan_action(params)
        self.assertEqual(act.kind, mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER)
        self.assertIsNone(act.value)


class MissedWindowGuardTests(unittest.TestCase):
    """The PLAN-TRANSFER window guard: a planned ejection node further ahead
    than the guard is the NAMED missed-window failure, never a silent
    parking-orbit autowarp (the exact loiter this lane exists to remove)."""

    def _plan_transfer_state(self):
        params = mlib.b5_params_from_dict(b17_params())
        state = mlib.b5_initial_state(params)
        return replace(state, phase=mlib.B5_PLAN_TRANSFER,
                       phase_entry_ut=5_000_000.0, launch_ut=4_999_000.0,
                       plan_attempts=1, last_plan_ut=5_000_000.0)

    def test_a_node_beyond_the_guard_assert_fails_with_the_named_reason(self):
        state, _ = mlib.b5_decide(self._plan_transfer_state(), snap(
            ut=5_000_100.0, node_count=1,
            node_ut=5_000_100.0 + 30_000.0, body="Kerbin"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("missed-ejection-window", state.loss_reason)

    def test_a_node_inside_the_guard_hands_off_and_stamps_the_evidence(self):
        state, actions = mlib.b5_decide(self._plan_transfer_state(), snap(
            ut=5_000_100.0, node_count=1,
            node_ut=5_000_100.0 + 1_200.0, body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        self.assertEqual(state.transfer_node_ut, 5_001_300.0)
        self.assertEqual(state.transfer_handoff_ut, 5_000_100.0)
        self.assertIn(mlib.ACTION_MJ_EXECUTE_NODES,
                      [a.kind for a in actions])

    def test_flag_off_never_consults_the_guard(self):
        params = mlib.b5_params_from_dict(b17_params(padAlignEjection=False))
        state = replace(mlib.b5_initial_state(params),
                        phase=mlib.B5_PLAN_TRANSFER,
                        phase_entry_ut=5_000_000.0, plan_attempts=1,
                        last_plan_ut=5_000_000.0)
        # A B7-shaped window wait can be ~200 days out; the guard must not
        # exist for it.
        handled, _ = mlib.b5_decide(state, snap(
            ut=5_000_100.0, node_count=1,
            node_ut=5_000_100.0 + 17_000_000.0, body="Kerbin"))
        self.assertEqual(handled.phase, mlib.B5_TRANSFER_BURN)
        self.assertIsNone(handled.verdict)


class PadAlignAssertionRowTests(unittest.TestCase):
    def _state_with(self, **fields):
        params = mlib.b5_params_from_dict(b17_params())
        return replace(mlib.b5_initial_state(params), **fields)

    def test_met_when_the_node_landed_within_the_guard_of_launch(self):
        state = self._state_with(launch_ut=100.0, transfer_node_ut=3_000.0,
                                 pad_align_window_ut=2_000.0,
                                 pad_align_jump_issued=True)
        rows = mlib.evaluate_b5_assertions(
            (), state.params, phases_reached=(), state=state)
        row = {r.name: r for r in rows}["padAlignedDirectEjection"]
        self.assertTrue(row.met)
        self.assertEqual(row.value, 2_900.0)

    def test_unmet_when_either_stamp_is_missing_fail_closed(self):
        state = self._state_with(launch_ut=None, transfer_node_ut=3_000.0)
        rows = mlib.evaluate_b5_assertions(
            (), state.params, phases_reached=(), state=state)
        row = {r.name: r for r in rows}["padAlignedDirectEjection"]
        self.assertFalse(row.met)

    def test_unmet_when_the_node_sat_a_loiter_away_from_launch(self):
        state = self._state_with(launch_ut=100.0,
                                 transfer_node_ut=100.0 + 100_000.0)
        rows = mlib.evaluate_b5_assertions(
            (), state.params, phases_reached=(), state=state)
        row = {r.name: r for r in rows}["padAlignedDirectEjection"]
        self.assertFalse(row.met)

    def test_the_row_is_absent_for_every_flag_off_lane(self):
        params = mlib.b5_params_from_dict(b17_params(padAlignEjection=False))
        rows = mlib.evaluate_b5_assertions(
            (), params, phases_reached=(),
            state=mlib.b5_initial_state(params))
        self.assertNotIn("padAlignedDirectEjection",
                         [r.name for r in rows])


class TimeJumpPollWindowTests(unittest.TestCase):
    def test_timejump_polls_longer_than_its_csharp_dispatch_budget(self):
        # The C# deferral budget is 120 s (hlib's per-verb table); polling for
        # exactly 120 s manufactures a TIMEOUT out of a healthy dispatch that
        # used its whole budget -- the ExitToSpaceCenter arithmetic.
        self.assertGreater(mlib.seam_command_poll_seconds("TimeJump"), 120.0)


class CraftDriftTests(unittest.TestCase):
    """WIRES THE BUILDER'S `--check` INTO THE SUITE, the way the GS-1 cell
    does for its craft: a hand edit to the committed DD1 .craft, or a change
    to the derivation, reds here instead of in a live forge flight."""

    @classmethod
    def setUpClass(cls):
        path = os.path.join(HARNESS_ROOT, "tools", "build_dd1_craft.py")
        spec = importlib.util.spec_from_file_location("build_dd1_craft", path)
        cls.builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.builder)

    def test_the_committed_craft_satisfies_every_post_condition(self):
        committed = self.builder.read_lines(self.builder.CRAFT_PATH)
        self.assertEqual(self.builder.verify(committed), [])

    def test_the_committed_craft_is_byte_identical_to_a_fresh_rebuild(self):
        committed = self.builder.read_lines(self.builder.CRAFT_PATH)
        built = self.builder.build()
        self.assertEqual(
            committed, built,
            "the committed DD1 craft has DRIFTED from the derivation; "
            "re-run build_dd1_craft.py --write and commit, or explain")

    def test_the_booster_carries_no_command_part(self):
        # Assumption A8: the split must author plain debris (a second
        # ModuleCommand would add a RewindPoint + child-recording structure
        # to the committed tree this fixture must not carry).
        text = "\n".join(self.builder.build())
        self.assertEqual(text.count("name = ModuleCommand"), 1)

    def test_the_staging_is_the_two_stage_hot_stage_contract(self):
        records = self.builder.part_records(self.builder.build())
        by_name = {}
        for name, rec in records:
            by_name.setdefault(name, []).append(rec)
        self.assertEqual(
            int(by_name["engineLargeSkipper.v2"][0]["istg"]),
            self.builder.STAGE_IGNITE)
        self.assertEqual(int(by_name["Decoupler.2"][0]["istg"]),
                         self.builder.STAGE_SEPARATE)
        self.assertEqual(int(by_name["liquidEngine3.v2"][0]["istg"]),
                         self.builder.STAGE_SEPARATE)


if __name__ == "__main__":
    unittest.main()
