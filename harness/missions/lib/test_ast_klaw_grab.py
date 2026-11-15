"""Unit cells for the Klaw lanes (mission `ast_klaw_grab`, scenarios AST-1 / AST-2 /
COM-1 / AST-3 / AST-4, fixtures built by harness/tools/build_ast_klaw_fixtures.py).

Four groups:

  1. THE PURE HELPERS: `parse_part_event_spec`, `pick_part_event`, `klaw_translation`
     (the RCS command's signs in kRPC's vessel frame, its clamp, deadband and NaN
     fail-closed) and `klaw_params_from_dict`.
  2. THE PURE MACHINE (`mlib.klaw_decide`): the arm / target / approach / hold order,
     the capture read off the part count, the optional drill / release / back-off /
     coast / dock phases, and a named give-up in each waiting phase.
  3. THE SHELL, driven end to end over a scripted flight with no krpc and no KSP.
  4. SPEC / SCHEMA SYNC for the five lanes: the mission name, the fixture, every
     required param present and none undeclared, the phases each lane configures,
     and the first post-mission WarpToUT past the mission's game-time bound.

NO krpc, NO KSP, NO network.
"""

import math
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mlib                        # noqa: E402
import mission_runner              # noqa: E402
import ast_klaw_grab               # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

SCHEMA_PATH = os.path.join(_MISSIONS, "ast_klaw_grab.schema.toml")
SCENARIOS = os.path.join(_HARNESS, "scenarios")

# lane id -> (fixture leaf, rock name, drill?, release?, dock target)
LANES = {
    "AST-1-klaw-grab-rewind": ("ast-klaw-lko", "Ast. VPA-167", False, False, ""),
    "AST-2-klaw-release": ("ast-klaw-lko", "Ast. VPA-167", False, True, ""),
    "COM-1-comet-klaw-grab": ("com-klaw-lko", "UnknownComet", False, False, ""),
    "AST-3-klaw-mine-route": ("ast-miner-lko", "Ast. VPA-167", True, True, "AST Depot"),
    "AST-4-klaw-refly": ("ast-mothership-lko", "Ast. VPA-167", False, False, ""),
}

# The fixtures' FLIGHTSTATE UT (build_ast_klaw_fixtures.py keeps lab-lko-sandbox's).
FIXTURE_UT = 355.83

NAN = float("nan")
FAR = (0.0, 60.0, 0.0)
STILL = (0.0, 0.0, 0.0)

PARAMS = mlib.KlawParams(start_settle_seconds=5.0, arm_seconds=5.0, hold_seconds=15.0,
                         settle_seconds=5.0)


def _k(ut, part_count=10, pos=FAR, vel=STILL, **kw):
    return snap(ut=ut, part_count=part_count, klaw_target_rel_position=pos,
                klaw_target_rel_velocity=vel, **kw)


# A grab only: arm, target, close, capture (10 -> 11 parts), hold, settle.
GRAB = [
    _k(100.0, pos=(NAN, NAN, NAN)),                        # KG-START entry
    _k(105.0, pos=(NAN, NAN, NAN)),                        # -> KG-ARM (arm event)
    _k(110.0, pos=(NAN, NAN, NAN)),                        # -> KG-TARGET (target rock)
    _k(111.0),                                             # -> KG-APPROACH
    _k(112.0, pos=(0.0, 40.0, 0.0), vel=(0.0, -0.5, 0.0)),
    _k(160.0, pos=(0.0, 2.5, 0.0), vel=(0.0, -0.15, 0.0)),
    _k(170.0, part_count=11, pos=(NAN, NAN, NAN)),         # capture -> KG-HOLD
    _k(180.0, part_count=11, pos=(NAN, NAN, NAN)),
    _k(186.0, part_count=11, pos=(NAN, NAN, NAN)),         # -> KG-SETTLE
    _k(192.0, part_count=11, pos=(NAN, NAN, NAN)),         # -> KG-TERMINAL
]


def _walk(frames, params=PARAMS):
    st = mlib.klaw_initial_state(params)
    log = []
    for f in frames:
        st, actions = mlib.klaw_decide(st, f)
        log.append((st.phase, list(actions)))
        if st.done:
            break
    return st, log


def _kinds(log):
    return [(ph, [a.kind for a in acts]) for ph, acts in log]


class PartEventSpecTests(unittest.TestCase):

    def test_parse_three_fields_stripped(self):
        self.assertEqual(mlib.parse_part_event_spec(" GrapplingDevice | ModuleAnimateGeneric | Arm "),
                         ("GrapplingDevice", "ModuleAnimateGeneric", "Arm"))

    def test_parse_allows_an_empty_module(self):
        self.assertEqual(mlib.parse_part_event_spec("GrapplingDevice||Release"),
                         ("GrapplingDevice", "", "Release"))

    def test_parse_rejects_malformed(self):
        for bad in ("", "GrapplingDevice|Arm", "a|b|c|d", "|ModuleX|Arm", "Part|ModuleX|",
                    None, 7):
            self.assertIsNone(mlib.parse_part_event_spec(bad), bad)

    def test_the_default_specs_parse(self):
        p = mlib.KlawParams()
        self.assertEqual(mlib.parse_part_event_spec(p.arm_event)[2], "Arm")
        self.assertEqual(mlib.parse_part_event_spec(p.release_event)[1], "ModuleGrappleNode")

    def test_pick_exact_then_prefix_then_substring_case_insensitive(self):
        events = ["Disarm", "Arm Grapple", "arm"]
        self.assertEqual(mlib.pick_part_event(events, "Arm"), "arm")
        self.assertEqual(mlib.pick_part_event(["Disarm", "Arm Grapple"], "arm"), "Arm Grapple")
        self.assertEqual(mlib.pick_part_event(["Disarm"], "ARM"), "Disarm")
        self.assertEqual(mlib.pick_part_event(["Release", "Free Pivot"], "release"), "Release")

    def test_pick_misses_and_empty_wanted(self):
        self.assertIsNone(mlib.pick_part_event(["Release"], "Arm"))
        self.assertIsNone(mlib.pick_part_event(["Arm"], ""))
        self.assertIsNone(mlib.pick_part_event(None, "Arm"))


class KlawTranslationTests(unittest.TestCase):

    def test_far_target_dead_ahead_at_rest_pushes_forward_at_the_clamp(self):
        self.assertEqual(mlib.klaw_translation(FAR, STILL, PARAMS), (0.0, 0.4, 0.0))

    def test_closing_faster_than_desired_brakes(self):
        # 3 m out (1 m above the standoff): desired = min_closing 0.15; closing at 0.8.
        r, f, u = mlib.klaw_translation((0.0, 3.0, 0.0), (0.0, -0.8, 0.0), PARAMS)
        self.assertLess(f, 0.0)
        self.assertEqual((r, u), (0.0, 0.0))

    def test_at_the_desired_closing_speed_forward_is_inside_the_deadband(self):
        _r, f, _u = mlib.klaw_translation((0.0, 3.0, 0.0), (0.0, -0.15, 0.0), PARAMS)
        self.assertEqual(f, 0.0)

    def test_lateral_drift_is_followed_on_x(self):
        # Target drifting to the right (+x): translate right to follow it.
        r, _f, _u = mlib.klaw_translation((0.0, 20.0, 0.0), (0.2, -0.3, 0.0), PARAMS)
        self.assertGreater(r, 0.0)
        r, _f, _u = mlib.klaw_translation((-3.0, 20.0, 0.0), (0.0, -0.3, 0.0), PARAMS)
        self.assertLess(r, 0.0)

    def test_up_is_minus_z(self):
        # kRPC vessel frame: z is DOWN. A target below (+z) and drifting down needs a
        # NEGATIVE `up` command (translate down).
        _r, _f, u = mlib.klaw_translation((0.0, 20.0, 4.0), (0.0, -0.3, 0.1), PARAMS)
        self.assertLess(u, 0.0)
        _r, _f, u = mlib.klaw_translation((0.0, 20.0, -4.0), (0.0, -0.3, -0.1), PARAMS)
        self.assertGreater(u, 0.0)

    def test_every_axis_is_clamped(self):
        r, f, u = mlib.klaw_translation((50.0, 200.0, -50.0), (5.0, 5.0, -5.0), PARAMS)
        for c in (r, f, u):
            self.assertLessEqual(abs(c), PARAMS.max_translation + 1e-12)

    def test_non_finite_input_commands_nothing(self):
        self.assertEqual(mlib.klaw_translation((NAN, 1.0, 0.0), STILL, PARAMS), (0.0, 0.0, 0.0))
        self.assertEqual(mlib.klaw_translation(FAR, (0.0, NAN, 0.0), PARAMS), (0.0, 0.0, 0.0))
        self.assertEqual(mlib.klaw_translation(None, STILL, PARAMS), (0.0, 0.0, 0.0))
        self.assertEqual(mlib.klaw_translation(mlib.NAN3, mlib.NAN3, PARAMS), (0.0, 0.0, 0.0))

    def test_the_snapshot_defaults_are_unread_nan(self):
        s = snap(ut=1.0)
        self.assertFalse(any(math.isfinite(c) for c in s.klaw_target_rel_position))
        self.assertFalse(any(math.isfinite(c) for c in s.klaw_target_rel_velocity))


class KlawParamsTests(unittest.TestCase):

    def test_defaults(self):
        p = mlib.klaw_params_from_dict({})
        self.assertEqual(p, mlib.KlawParams())
        self.assertEqual(p.drill_seconds, 0.0)
        self.assertFalse(p.release)
        self.assertEqual(p.dock_target_name, "")

    def test_every_key_maps(self):
        d = {"rockName": "UnknownComet", "armEvent": "A|B|C", "releaseEvent": "D|E|F",
             "startSettleSeconds": 1, "armSeconds": 2, "targetTimeoutSeconds": 3,
             "standoffMeters": 44, "approachGain": 0.1, "minClosingSpeed": 0.1,
             "maxClosingSpeed": 0.5, "velocityGain": 0.7, "lateralGain": 0.2,
             "maxTranslation": 0.3, "translationDeadband": 0.01,
             "approachTimeoutSeconds": 400, "approachAbortExtraMeters": 20,
             "holdSeconds": 9, "drillSeconds": 30, "drillPart": "RadialDrill",
             "drillDeploySeconds": 4, "seamTimeoutSeconds": 50, "release": True,
             "releaseTimeoutSeconds": 25, "backoffSeconds": 8, "backoffTranslation": 0.2,
             "coastSeconds": 12, "dockTargetName": "AST Depot",
             "dockTimeoutSeconds": 700, "settleSeconds": 6}
        p = mlib.klaw_params_from_dict(d)
        self.assertEqual(
            (p.rock_name, p.arm_event, p.release_event, p.start_settle_seconds,
             p.arm_seconds, p.target_timeout, p.standoff_meters, p.approach_gain,
             p.min_closing_speed, p.max_closing_speed, p.velocity_gain, p.lateral_gain,
             p.max_translation, p.translation_deadband, p.approach_timeout,
             p.approach_abort_extra_meters, p.hold_seconds, p.drill_seconds, p.drill_part,
             p.drill_deploy_seconds, p.seam_timeout, p.release, p.release_timeout,
             p.backoff_seconds, p.backoff_translation, p.coast_seconds,
             p.dock_target_name, p.bdock.dock_timeout, p.settle_seconds),
            ("UnknownComet", "A|B|C", "D|E|F", 1.0, 2.0, 3.0, 44.0, 0.1, 0.1, 0.5, 0.7,
             0.2, 0.3, 0.01, 400.0, 20.0, 9.0, 30.0, "RadialDrill", 4.0, 50.0, True,
             25.0, 8.0, 0.2, 12.0, "AST Depot", 700.0, 6.0))


class KlawMachineTests(unittest.TestCase):

    def test_grab_only_happy_path_in_order(self):
        st, log = _walk(GRAB)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict, st.flake_reason)
        self.assertEqual(st.phases_reached, (
            mlib.KLAW_START, mlib.KLAW_ARM, mlib.KLAW_TARGET, mlib.KLAW_APPROACH,
            mlib.KLAW_HOLD, mlib.KLAW_SETTLE, mlib.KLAW_TERMINAL))
        self.assertTrue(st.grabbed)
        self.assertEqual(st.grab_ut, 170.0)
        self.assertEqual((st.baseline_part_count, st.held_part_count), (10, 11))
        self.assertEqual(st.approach_start_distance, 60.0)
        self.assertEqual(st.closest_distance, 2.5)
        outcomes = mlib.evaluate_klaw_assertions([], PARAMS, st.phases_reached, st)
        self.assertEqual([o.name for o in outcomes], ["grabbed"])
        self.assertTrue(outcomes[0].met)

    def test_grab_only_actions(self):
        _st, log = _walk(GRAB)
        arm = log[1][1]
        self.assertEqual([(a.kind, a.text) for a in arm],
                         [(mlib.ACTION_TRIGGER_PART_EVENT, PARAMS.arm_event)])
        tgt = log[2][1]
        self.assertEqual([(a.kind, a.text) for a in tgt],
                         [(mlib.ACTION_TARGET_NEAREST_NAMED_VESSEL, "Ast. VPA-167")])
        self.assertEqual([a.kind for a in log[3][1]],
                         [mlib.ACTION_SAS_TARGET, mlib.ACTION_SET_RCS])
        # Every approach frame commands a translation; the capture zeroes it.
        self.assertEqual([a.kind for a in log[4][1]], [mlib.ACTION_SET_TRANSLATION])
        self.assertEqual(log[4][1][0].translation[1] > 0.0, True)
        cap = log[6]
        self.assertEqual(cap[0], mlib.KLAW_HOLD)
        self.assertEqual([(a.kind, a.translation) for a in cap[1]],
                         [(mlib.ACTION_SET_TRANSLATION, mlib.ZERO_TRANSLATION)])
        # Nothing ever releases or drills on a grab-only flight.
        kinds = [a.kind for _p, acts in log for a in acts]
        self.assertNotIn(mlib.ACTION_PARSEK_SEAM_COMMAND, kinds)
        self.assertEqual(kinds.count(mlib.ACTION_TRIGGER_PART_EVENT), 1)

    def test_start_waits_its_dwell(self):
        st = mlib.klaw_initial_state(PARAMS)
        st, a = mlib.klaw_decide(st, _k(100.0))
        self.assertEqual((st.phase, a), (mlib.KLAW_START, []))
        st, a = mlib.klaw_decide(st, _k(104.9))
        self.assertEqual((st.phase, a), (mlib.KLAW_START, []))
        st, a = mlib.klaw_decide(st, _k(105.0))
        self.assertEqual(st.phase, mlib.KLAW_ARM)

    def test_target_needs_a_finite_position_and_a_part_count(self):
        st = mlib.klaw_initial_state(PARAMS)
        for f in GRAB[:3]:
            st, _a = mlib.klaw_decide(st, f)
        self.assertEqual(st.phase, mlib.KLAW_TARGET)
        st, a = mlib.klaw_decide(st, _k(111.0, part_count=0))
        self.assertEqual((st.phase, a), (mlib.KLAW_TARGET, []))
        st, a = mlib.klaw_decide(st, _k(112.0, pos=(NAN, 1.0, 1.0)))
        self.assertEqual((st.phase, a), (mlib.KLAW_TARGET, []))

    def test_target_give_up_is_named(self):
        st = mlib.klaw_initial_state(PARAMS)
        for f in GRAB[:3]:
            st, _a = mlib.klaw_decide(st, f)
        st, _a = mlib.klaw_decide(st, _k(141.0, pos=(NAN, NAN, NAN)))
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(st.flake_phase, mlib.KLAW_TARGET)
        self.assertIn("Ast. VPA-167", st.flake_reason)

    def _approaching(self):
        st = mlib.klaw_initial_state(PARAMS)
        for f in GRAB[:4]:
            st, _a = mlib.klaw_decide(st, f)
        self.assertEqual(st.phase, mlib.KLAW_APPROACH)
        return st

    def test_approach_timeout_flakes_and_zeroes_translation(self):
        st = self._approaching()
        st, a = mlib.klaw_decide(st, _k(412.0, pos=(0.0, 30.0, 0.0)))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(st.flake_phase, mlib.KLAW_APPROACH)
        self.assertIn("no capture within 300 s", st.flake_reason)
        self.assertEqual([(x.kind, x.translation) for x in a],
                         [(mlib.ACTION_SET_TRANSLATION, mlib.ZERO_TRANSLATION)])
        outcomes = mlib.evaluate_klaw_assertions([], PARAMS, st.phases_reached, st)
        self.assertFalse(outcomes[0].met)

    def test_a_receding_target_flakes_named(self):
        st = self._approaching()
        st, a = mlib.klaw_decide(st, _k(130.0, pos=(0.0, 91.0, 0.0)))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("receding", st.flake_reason)
        self.assertEqual(a[0].translation, mlib.ZERO_TRANSLATION)

    def test_a_part_count_drop_is_not_a_capture(self):
        st = self._approaching()
        st, a = mlib.klaw_decide(st, _k(120.0, part_count=9, pos=(0.0, 30.0, 0.0)))
        self.assertEqual(st.phase, mlib.KLAW_APPROACH)
        self.assertFalse(st.grabbed)

    def test_an_unread_target_mid_approach_commands_zero(self):
        st = self._approaching()
        st, a = mlib.klaw_decide(st, _k(120.0, pos=(NAN, NAN, NAN)))
        self.assertEqual(st.phase, mlib.KLAW_APPROACH)
        self.assertEqual([(x.kind, x.translation) for x in a],
                         [(mlib.ACTION_SET_TRANSLATION, (0.0, 0.0, 0.0))])

    def test_hold_waits_its_full_dwell(self):
        st, log = _walk(GRAB[:8])
        self.assertEqual(st.phase, mlib.KLAW_HOLD)
        self.assertFalse(st.done)

    def test_done_state_is_inert(self):
        st, _log = _walk(GRAB)
        st2, a = mlib.klaw_decide(st, _k(500.0))
        self.assertIs(st2, st)
        self.assertEqual(a, [])

    def test_assertions_fail_without_a_capture(self):
        st = mlib.klaw_initial_state(PARAMS)
        out = mlib.evaluate_klaw_assertions([], PARAMS, (), st)
        self.assertEqual([(o.name, o.met) for o in out], [("grabbed", False)])


def _seam(ut, tag, result, part_count=11):
    return _k(ut, part_count=part_count, pos=(NAN, NAN, NAN),
              seam_command_tag=tag, seam_command_result=result)


DRILL_PARAMS = mlib.KlawParams(start_settle_seconds=5.0, arm_seconds=5.0,
                               hold_seconds=15.0, drill_seconds=30.0,
                               drill_deploy_seconds=6.0, release=True,
                               backoff_seconds=10.0, coast_seconds=10.0,
                               settle_seconds=5.0)


class KlawDrillReleaseTests(unittest.TestCase):

    def _held(self, params=DRILL_PARAMS):
        st = mlib.klaw_initial_state(params)
        for f in GRAB[:7]:
            st, _a = mlib.klaw_decide(st, f)
        self.assertEqual(st.phase, mlib.KLAW_HOLD)
        return st

    def test_drill_then_release_then_backoff_coast_settle(self):
        st = self._held()
        st, a = mlib.klaw_decide(st, _k(185.0, part_count=11))
        self.assertEqual(st.phase, mlib.KLAW_DRILL)
        self.assertEqual([(x.kind, x.seam_verb, x.seam_args, x.seam_tag) for x in a], [(
            mlib.ACTION_PARSEK_SEAM_COMMAND, "PartAction",
            (("button", "deploy"), ("part", "MiniDrill"), ("all", "true")),
            "kg-drill-deploy")])
        # A stale result of another command never advances the step.
        st, a = mlib.klaw_decide(st, _seam(186.0, "other", "OK"))
        self.assertEqual((st.drill_step, a), (0, []))
        # OK, but the deploy dwell runs from the step's issue.
        st, a = mlib.klaw_decide(st, _seam(188.0, "kg-drill-deploy", "OK"))
        self.assertEqual((st.drill_step, a), (0, []))
        st, a = mlib.klaw_decide(st, _seam(191.0, "kg-drill-deploy", "OK"))
        self.assertEqual(st.drill_step, 1)
        self.assertEqual(a[0].seam_args[0], ("button", "startconverter"))
        self.assertNotIn("converter", dict(a[0].seam_args))
        st, a = mlib.klaw_decide(st, _seam(192.0, "kg-drill-start", "OK"))
        self.assertEqual((st.drill_step, a), (1, []))
        st, a = mlib.klaw_decide(st, _seam(221.0, "kg-drill-start", "OK"))
        self.assertEqual(st.drill_step, 2)
        self.assertEqual(a[0].seam_args[0], ("button", "stopconverter"))
        st, a = mlib.klaw_decide(st, _seam(222.0, "kg-drill-stop", "OK"))
        self.assertTrue(st.drilled)
        self.assertEqual(st.phase, mlib.KLAW_RELEASE)
        self.assertEqual([(x.kind, x.text) for x in a],
                         [(mlib.ACTION_TRIGGER_PART_EVENT, DRILL_PARAMS.release_event)])
        # The release: the part count falls below the held count.
        st, a = mlib.klaw_decide(st, _k(223.0, part_count=11, pos=(NAN, NAN, NAN)))
        self.assertEqual((st.phase, a), (mlib.KLAW_RELEASE, []))
        st, a = mlib.klaw_decide(st, _k(224.0, part_count=10, pos=(NAN, NAN, NAN)))
        self.assertEqual(st.phase, mlib.KLAW_BACKOFF)
        self.assertTrue(st.released)
        self.assertEqual(a[0].translation, (0.0, -0.3, 0.0))
        st, a = mlib.klaw_decide(st, _k(234.0, part_count=10))
        self.assertEqual(st.phase, mlib.KLAW_COAST)
        self.assertEqual(a[0].translation, mlib.ZERO_TRANSLATION)
        st, a = mlib.klaw_decide(st, _k(244.0, part_count=10))
        self.assertEqual((st.phase, a), (mlib.KLAW_SETTLE, []))
        st, a = mlib.klaw_decide(st, _k(249.0, part_count=10))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict, st.flake_reason)
        out = mlib.evaluate_klaw_assertions([], DRILL_PARAMS, st.phases_reached, st)
        self.assertEqual([(o.name, o.met) for o in out],
                         [("grabbed", True), ("drilled", True), ("released", True)])

    def _drilling_at_last_step(self):
        st = self._held()
        st, _a = mlib.klaw_decide(st, _k(185.0, part_count=11))
        st, _a = mlib.klaw_decide(st, _seam(191.0, "kg-drill-deploy", "OK"))
        st, _a = mlib.klaw_decide(st, _seam(221.0, "kg-drill-start", "OK"))
        self.assertEqual(st.drill_step, 2)
        return st

    def test_an_error_on_the_stop_press_still_releases_but_is_not_drilled(self):
        st = self._drilling_at_last_step()
        st, _a = mlib.klaw_decide(st, _seam(222.0, "kg-drill-stop", "ERROR"))
        self.assertEqual(st.phase, mlib.KLAW_RELEASE)
        self.assertFalse(st.drilled)

    def test_an_error_on_an_earlier_step_flakes_named(self):
        st = self._held()
        st, _a = mlib.klaw_decide(st, _k(185.0, part_count=11))
        st, _a = mlib.klaw_decide(st, _seam(186.0, "kg-drill-deploy", "ERROR"))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("kg-drill-deploy returned ERROR", st.flake_reason)

    def test_a_drill_step_that_never_answers_flakes_named(self):
        st = self._held()
        st, _a = mlib.klaw_decide(st, _k(185.0, part_count=11))
        st, _a = mlib.klaw_decide(st, _k(246.0, part_count=11))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("never answered", st.flake_reason)

    def test_no_release_observed_flakes_named(self):
        st = self._drilling_at_last_step()
        st, _a = mlib.klaw_decide(st, _seam(222.0, "kg-drill-stop", "OK"))
        st, _a = mlib.klaw_decide(st, _k(253.0, part_count=11))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(st.flake_phase, mlib.KLAW_RELEASE)
        self.assertIn("no release observed", st.flake_reason)

    def test_release_without_drill_skips_the_drill(self):
        params = mlib.KlawParams(start_settle_seconds=5.0, arm_seconds=5.0,
                                 hold_seconds=15.0, release=True)
        st = self._held(params)
        st, a = mlib.klaw_decide(st, _k(185.0, part_count=11))
        self.assertEqual(st.phase, mlib.KLAW_RELEASE)
        self.assertNotIn(mlib.KLAW_DRILL, st.phases_reached)
        out = mlib.evaluate_klaw_assertions([], params, st.phases_reached, st)
        self.assertEqual([o.name for o in out], ["grabbed", "released"])


DOCK_PARAMS = mlib.klaw_params_from_dict({
    "startSettleSeconds": 5, "armSeconds": 5, "holdSeconds": 15, "release": True,
    "backoffSeconds": 10, "coastSeconds": 10, "dockTargetName": "AST Depot",
    "settleSeconds": 5})


class KlawDockTests(unittest.TestCase):

    def _coasting_done(self):
        st = mlib.klaw_initial_state(DOCK_PARAMS)
        for f in GRAB[:7]:
            st, _a = mlib.klaw_decide(st, f)
        st, _a = mlib.klaw_decide(st, _k(185.0, part_count=11))      # -> RELEASE
        st, _a = mlib.klaw_decide(st, _k(186.0, part_count=10))      # -> BACKOFF
        st, _a = mlib.klaw_decide(st, _k(196.0, part_count=10))      # -> COAST
        st, a = mlib.klaw_decide(st, _k(206.0, part_count=10))       # -> DOCK-TGT
        self.assertEqual(st.phase, mlib.KLAW_DOCK_TARGET)
        self.assertEqual([(x.kind, x.text) for x in a], [
            (mlib.ACTION_SET_SAS, None), (mlib.ACTION_CONTROL_FROM_DOCKING_PORT, None),
            (mlib.ACTION_TARGET_NEAREST_NAMED_VESSEL, "AST Depot")])
        return st

    def test_dock_target_hands_off_to_bdock_and_completes_on_a_docked_transition(self):
        st = self._coasting_done()
        st, a = mlib.klaw_decide(st, _k(207.0, part_count=10, docking_state="Ready",
                                        target_set=True, target_distance=150.0))
        self.assertEqual(st.phase, mlib.BDOCK_DOCK)
        self.assertEqual([x.kind for x in a], [
            mlib.ACTION_SET_SAS, mlib.ACTION_SET_RCS, mlib.ACTION_SET_TARGET_DOCKING_PORT])
        st, _a = mlib.klaw_decide(st, _k(208.0, part_count=10, docking_state="Ready",
                                         target_set=True, target_distance=150.0))
        st, a = mlib.klaw_decide(st, _k(400.0, part_count=14, docking_state="Docked",
                                        target_set=True, target_distance=0.3))
        self.assertEqual(st.phase, mlib.KLAW_SETTLE)
        self.assertTrue(st.docked)
        self.assertEqual([x.kind for x in a], [mlib.ACTION_MJ_DISABLE_DOCKING])
        st, _a = mlib.klaw_decide(st, _k(405.0, part_count=14))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict, st.flake_reason)
        out = mlib.evaluate_klaw_assertions([], DOCK_PARAMS, st.phases_reached, st)
        self.assertEqual([(o.name, o.met) for o in out],
                         [("grabbed", True), ("released", True), ("docked", True)])

    def test_no_dock_target_flakes_named(self):
        st = self._coasting_done()
        st, _a = mlib.klaw_decide(st, _k(237.0, part_count=10))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("AST Depot", st.flake_reason)

    def test_without_a_dock_target_the_coast_settles(self):
        params = mlib.KlawParams(start_settle_seconds=5.0, arm_seconds=5.0,
                                 hold_seconds=15.0, release=True)
        st = mlib.klaw_initial_state(params)
        for f in GRAB[:7]:
            st, _a = mlib.klaw_decide(st, f)
        st, _a = mlib.klaw_decide(st, _k(185.0, part_count=11))
        st, _a = mlib.klaw_decide(st, _k(186.0, part_count=10))
        st, _a = mlib.klaw_decide(st, _k(196.0, part_count=10))
        st, _a = mlib.klaw_decide(st, _k(206.0, part_count=10))
        self.assertEqual(st.phase, mlib.KLAW_SETTLE)
        self.assertNotIn(mlib.KLAW_DOCK_TARGET, st.phases_reached)


class KlawShellTests(unittest.TestCase):

    def test_shell_flies_the_scripted_grab_to_mission_ok(self):
        control = FakeMissionControl(GRAB)
        params = {"rockName": "Ast. VPA-167", "approachTimeoutSeconds": 300,
                  "holdSeconds": 15, "startSettleSeconds": 5, "armSeconds": 5,
                  "settleSeconds": 5}
        code, result = run(ast_klaw_grab.SPEC, params, control, budget=9000.0)
        self.assertEqual(result["mission"], "ast_klaw_grab")
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_TRIGGER_PART_EVENT, kinds)
        self.assertIn(mlib.ACTION_SAS_TARGET, kinds)
        self.assertIn(mlib.ACTION_SET_TRANSLATION, kinds)

    def test_the_shell_never_warps(self):
        self.assertFalse(ast_klaw_grab.SPEC.allow_rails_warp)
        self.assertEqual(ast_klaw_grab.SPEC.max_physics_warp, 0.0)


def _load_spec(lane):
    with open(os.path.join(SCENARIOS, lane + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _schema():
    with open(SCHEMA_PATH, "rb") as fh:
        return tomllib.load(fh)["params"]


def _mission_bound(start_ut, p):
    """The latest game UT a MISSION-OK run can end at: the mission never warps, so it
    ends no later than its start plus every configured phase's give-up budget."""
    b = (start_ut + p.start_settle_seconds + p.arm_seconds + p.target_timeout
         + p.approach_timeout + p.hold_seconds + p.settle_seconds)
    if p.drill_seconds > 0:
        b += 3 * p.seam_timeout + p.drill_deploy_seconds + p.drill_seconds
    if p.release:
        b += p.release_timeout + p.backoff_seconds + p.coast_seconds
    if p.dock_target_name:
        b += p.target_timeout + p.bdock.dock_timeout
    return b


class KlawSpecSyncTests(unittest.TestCase):

    def test_every_lane_names_this_mission_its_fixture_and_satisfies_the_schema(self):
        schema = _schema()
        for lane, (fixture, rock, _d, _r, _t) in LANES.items():
            with self.subTest(lane=lane):
                spec = _load_spec(lane)
                self.assertEqual(spec["id"], lane)
                self.assertEqual(spec["driver"]["kind"], "autopilot")
                self.assertEqual(spec["driver"]["mission"], "ast_klaw_grab")
                self.assertEqual(spec["fixture"]["saveTemplate"], "fixtures/saves/" + fixture)
                params = spec["driver"]["missionParams"]
                for key, rule in schema.items():
                    if rule.get("required"):
                        self.assertIn(key, params, key)
                for key in params:
                    self.assertIn(key, schema, "undeclared mission param %s" % key)
                self.assertEqual(params["rockName"], rock)

    def test_each_lane_configures_exactly_its_phases(self):
        for lane, (_f, _rock, drill, release, target) in LANES.items():
            with self.subTest(lane=lane):
                p = mlib.klaw_params_from_dict(_load_spec(lane)["driver"]["missionParams"])
                self.assertEqual(p.drill_seconds > 0, drill)
                self.assertEqual(p.release, release)
                self.assertEqual(p.dock_target_name, target)

    def test_exactly_one_mission_step_after_a_load(self):
        for lane in LANES:
            with self.subTest(lane=lane):
                steps = _load_spec(lane)["driver"]["steps"]
                missions = [i for i, s in enumerate(steps) if s.get("phase") == "mission"]
                self.assertEqual(len(missions), 1)
                self.assertEqual(steps[missions[0]]["expect"], "MISSION-OK")
                self.assertTrue(any(s.get("cmd") == "LoadGame" for s in steps[:missions[0]]))

    def test_the_first_post_mission_warp_is_past_the_mission_bound(self):
        # A post-mission WarpToUT below the bound could be a backward warp on a slow
        # MISSION-OK run (`warptout refused`), unless a rewind or a load re-based the
        # clock first.
        resets = ("InvokeRewindToLaunch", "InvokeRewind", "LoadGame")
        for lane in LANES:
            with self.subTest(lane=lane):
                spec = _load_spec(lane)
                steps = spec["driver"]["steps"]
                m = next(i for i, s in enumerate(steps) if s.get("phase") == "mission")
                pre = [float(s["args"]["ut"]) for s in steps[:m] if s.get("cmd") == "WarpToUT"]
                start = max(pre) if pre else FIXTURE_UT
                p = mlib.klaw_params_from_dict(spec["driver"]["missionParams"])
                bound = _mission_bound(start, p)
                for s in steps[m + 1:]:
                    if s.get("cmd") in resets:
                        break
                    if s.get("cmd") in ("WarpToUT", "TimeJump") and "ut" in s.get("args", {}):
                        self.assertGreater(float(s["args"]["ut"]), bound + 30.0, lane)
                        break

    def test_every_lane_is_operator_tier_and_retries_once(self):
        for lane in LANES:
            with self.subTest(lane=lane):
                spec = _load_spec(lane)
                self.assertEqual(spec["tier"], "operator")
                self.assertEqual(spec["retry"]["policy"], "once")


class _TgVessel(object):
    def __init__(self, name, pos=(30.0, 40.0, 0.0)):
        self.name = name
        self._pos = pos
        self.reference_frame = object()

    def position(self, frame):
        return self._pos


class _TgSc(object):
    """kRPC's SpaceCenter as the Klaw lanes see it: ``vessels`` leaves the rock out (kRPC
    filters SpaceObject vessels), ``target_vessel`` returns the target the save restored."""

    def __init__(self, vessels, target):
        self.vessels = vessels
        self.target_vessel = target


class StockTargetFallbackTests(unittest.TestCase):
    def setUp(self):
        self.lines = []
        self._sink = mission_runner._stdout_sink
        mission_runner._stdout_sink = self.lines.append

    def tearDown(self):
        mission_runner._stdout_sink = self._sink

    def _target(self, sc, active, name):
        ctrl = mission_runner.KrpcMissionControl(client_name="t", read_klaw=True)
        ctrl._target_nearest_named_vessel(sc, active, name)
        return ctrl

    def test_the_pure_match(self):
        self.assertTrue(mlib.stock_target_matches("Ast. VPA-167", False, "Ast. VPA-167"))
        self.assertFalse(mlib.stock_target_matches("Ast. VPA-167", True, "Ast. VPA-167"))
        self.assertFalse(mlib.stock_target_matches("AST Watcher", False, "Ast. VPA-167"))
        self.assertFalse(mlib.stock_target_matches(None, False, "Ast. VPA-167"))

    def test_scan_names_are_listed_with_the_active_marked_and_capped(self):
        c = [("AST Klaw", float("nan"), True), ("AST Watcher", float("nan"), False)]
        self.assertEqual("'AST Klaw'*, 'AST Watcher'", mlib.format_scan_names(c))
        many = [("v%d" % i, 0.0, False) for i in range(10)]
        self.assertTrue(mlib.format_scan_names(many, limit=3).endswith("'v2' +7 more"))
        self.assertEqual("none", mlib.format_scan_names([]))

    def test_a_rock_kRPC_does_not_list_is_captured_from_the_stock_target(self):
        probe, watcher, rock = _TgVessel("AST Klaw"), _TgVessel("AST Watcher"), \
            _TgVessel("Ast. VPA-167")
        ctrl = self._target(_TgSc([probe, watcher], rock), probe, "Ast. VPA-167")
        self.assertIs(rock, ctrl._station_vessel)
        log = "\n".join(self.lines)
        self.assertIn("targeted 'Ast. VPA-167' through the stock target at 50.0 m", log)
        self.assertNotIn("no loaded vessel", log)

    def test_a_listed_vessel_still_wins_over_the_stock_target(self):
        probe, rock_listed, other = _TgVessel("AST Klaw"), _TgVessel("Ast. VPA-167"), \
            _TgVessel("AST Watcher")
        ctrl = self._target(_TgSc([probe, rock_listed], other), probe, "Ast. VPA-167")
        self.assertIs(rock_listed, ctrl._station_vessel)

    def test_a_wrong_stock_target_is_a_logged_miss_naming_the_scan(self):
        probe, watcher = _TgVessel("AST Klaw"), _TgVessel("AST Watcher")
        ctrl = self._target(_TgSc([probe, watcher], watcher), probe, "Ast. VPA-167")
        self.assertIsNone(ctrl._station_vessel)
        log = "\n".join(self.lines)
        self.assertIn("stock target 'AST Watcher' is not 'Ast. VPA-167'", log)
        self.assertIn("(2 vessel(s) scanned: 'AST Klaw'*, 'AST Watcher'; the stock target "
                      "is not it either)", log)


if __name__ == "__main__":
    unittest.main()
