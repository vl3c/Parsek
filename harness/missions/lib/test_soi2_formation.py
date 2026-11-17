"""Unit cells for SOI-1's FORMATION opt-in and the SOI-2 lane that flies it.

SOI-2 (todo RECORDER-PHYSICS-SOI-NO-SECTION-SPLIT residue, NEXT-TESTS-TO-BUILD-2026-11-11
item 9) reuses `soi1_mun_exit_burn` with `formationStageLeadSeconds` > 0: the approach
drops the lower stack shortly before the Mun SOI exit and the pod coasts through the
boundary at 1x beside it, so a LOADED background vessel (the dropped stack) crosses the
edge in physics with the active one. Pinned here:

  1. The opt-in absent, APPROACH still ignites the straddle burn (SOI-1 unchanged).
  2. The opt-in present: one stage at the lead, never a straddle ignition, the crossing
     stamped, the trail ends the run, the row reads the stage-to-crossing age.
  3. The named ends: a crossing before the stage, a warp frame, an unexpected body, the
     formation timeout.
  4. The shell round trip and the SOI-2 spec's params against the schema.

NO krpc, NO KSP, NO network.
"""

import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
_HARNESS = os.path.dirname(_MISSIONS)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import mlib  # noqa: E402
import soi1_mun_exit_burn as shell  # noqa: E402
from test_shells import FakeMissionControl, run  # noqa: E402
from test_soi1_mun_exit_burn import kinds, params, snap, step, to_approach  # noqa: E402

SPEC = os.path.join(_HARNESS, "scenarios", "SOI-2-mun-exit-formation-wingman.toml")
SCHEMA = os.path.join(_MISSIONS, "soi1_mun_exit_burn.schema.toml")
FORMATION = dict(formationStageLeadSeconds=25.0, formationMaxStageAgeSeconds=50.0)


def to_formation():
    st = to_approach(params(**FORMATION))
    st, acts = step(st, snap(ut=7700.0, time_to_soi=110.0))
    assert st.phase == mlib.SOI1_APPROACH and acts == [], (st.phase, acts)
    st, acts = step(st, snap(ut=7786.0, time_to_soi=24.0))
    assert st.phase == mlib.SOI1_FORMATION, st.phase
    return st, acts


class OptInTests(unittest.TestCase):
    def test_absent_the_approach_still_ignites_the_straddle(self):
        p = params()
        self.assertFalse(p.formation)
        st = to_approach(p)
        st, acts = step(st, snap(ut=7770.0, time_to_soi=40.0))
        self.assertEqual(mlib.SOI1_STRADDLE, st.phase)
        self.assertEqual([mlib.ACTION_SET_THROTTLE], kinds(acts))

    def test_absent_the_crossing_row_is_the_thrust_row(self):
        names = [r.name for r in mlib.evaluate_soi1_assertions([], params(), None)]
        self.assertIn("crossedUnderThrustAt1x", names)
        self.assertNotIn("crossedInFormationAt1x", names)


class FormationWalkTests(unittest.TestCase):
    def test_the_approach_stages_once_at_the_lead_and_never_ignites(self):
        st = to_approach(params(**FORMATION))
        st, acts = step(st, snap(ut=7770.0, time_to_soi=40.0))
        self.assertEqual(mlib.SOI1_APPROACH, st.phase)
        self.assertEqual([], acts)
        st, acts = step(st, snap(ut=7786.0, time_to_soi=24.0))
        self.assertEqual(mlib.SOI1_FORMATION, st.phase)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_ACTIVATE_STAGE], kinds(acts))
        self.assertEqual((7786.0, 24.0, 1.0), (st.formation_stage_ut,
                                               st.formation_stage_time_to_soi,
                                               st.formation_stage_warp_rate))

    def test_the_coast_crosses_and_the_trail_ends_the_run(self):
        st, _ = to_formation()
        st, acts = step(st, snap(ut=7800.0, time_to_soi=10.0))
        self.assertEqual([], acts)
        st, _ = step(st, snap(ut=7810.5, body="Kerbin", time_to_soi=float("nan")))
        self.assertEqual(7810.5, st.cross_ut)
        self.assertEqual(7800.0, st.last_home_ut)
        st, acts = step(st, snap(ut=7841.0, body="Kerbin", time_to_soi=float("nan")))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.SOI1_DONE, st.phase)
        self.assertEqual([mlib.ACTION_AP_DISENGAGE], kinds(acts))
        self.assertNotIn(mlib.SOI1_STRADDLE, st.phases_reached)
        rows = {r.name: r for r in mlib.evaluate_soi1_assertions([], st.params, st)}
        self.assertNotIn("crossedUnderThrustAt1x", rows)
        self.assertTrue(rows["crossedInFormationAt1x"].met, rows["crossedInFormationAt1x"])
        self.assertAlmostEqual(24.5, rows["crossedInFormationAt1x"].detail["stageAgeAtCrossing"])
        self.assertTrue(rows["endedInParentSoi"].met)

    def test_a_warp_frame_is_cancelled_and_reds_the_row(self):
        st, _ = to_formation()
        st, acts = step(st, snap(ut=7800.0, time_to_soi=10.0, warp_mode="PHYSICS",
                                 warp_rate=2.0))
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(acts))
        st, _ = step(st, snap(ut=7810.5, body="Kerbin", time_to_soi=float("nan")))
        st, _ = step(st, snap(ut=7841.0, body="Kerbin", time_to_soi=float("nan")))
        rows = {r.name: r for r in mlib.evaluate_soi1_assertions([], st.params, st)}
        self.assertFalse(rows["crossedInFormationAt1x"].met)

    def test_a_stage_too_old_at_the_crossing_reds_the_row(self):
        p = params(formationStageLeadSeconds=25.0, formationMaxStageAgeSeconds=20.0)
        st = to_approach(p)
        st, _ = step(st, snap(ut=7786.0, time_to_soi=24.0))
        st, _ = step(st, snap(ut=7810.5, body="Kerbin", time_to_soi=float("nan")))
        st, _ = step(st, snap(ut=7841.0, body="Kerbin", time_to_soi=float("nan")))
        rows = {r.name: r for r in mlib.evaluate_soi1_assertions([], st.params, st)}
        self.assertFalse(rows["crossedInFormationAt1x"].met)


class FormationNamedEndTests(unittest.TestCase):
    def test_a_crossing_before_the_stage_fails(self):
        st = to_approach(params(**FORMATION))
        st, acts = step(st, snap(ut=7800.0, body="Kerbin", time_to_soi=float("nan")))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("before the straddle ignition", st.loss_reason)

    def test_an_unexpected_body_fails(self):
        st, _ = to_formation()
        st, acts = step(st, snap(ut=7800.0, body="Minmus"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("unexpected body Minmus during the formation coast", st.loss_reason)
        self.assertIn(mlib.ACTION_CUT_THROTTLE, kinds(acts))

    def test_the_formation_coast_is_bounded(self):
        st, _ = to_formation()
        # Soi1Params' default straddleTimeoutSeconds (300) bounds the formation coast.
        st, _ = step(st, snap(ut=7786.0 + 301.0, time_to_soi=5.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("formation coast exceeded 300", st.loss_reason)

    def test_a_lost_soi_prediction_before_the_crossing_fails(self):
        st, _ = to_formation()
        st, _ = step(st, snap(ut=7800.0, time_to_soi=float("nan")))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("no SOI change on the trajectory after the stage", st.loss_reason)

    def test_the_formation_phase_runs_at_1x_only(self):
        st = mlib.Soi1State(params=params(**FORMATION), phase=mlib.SOI1_FORMATION)
        self.assertEqual(0.0, mlib.soi1_max_physics_warp(st))
        self.assertFalse(mlib.soi1_rails_warp_permitted(st))


class ShellAndSpecTests(unittest.TestCase):
    def _frames(self):
        f = [snap(ut=1000.0)]
        f += [snap(ut=1001.0 + i, warp_mode="PHYSICS", warp_rate=2.0) for i in range(3)]
        f += [snap(ut=1004.0)]
        f += [snap(ut=1010.0, available_thrust=125000.0, throttle=0.5, eccentricity=0.6),
              snap(ut=1030.0, available_thrust=125000.0, throttle=0.5, eccentricity=1.21,
                   time_to_soi=6780.0, next_body="Kerbin"),
              snap(ut=1031.0, time_to_soi=6779.0),
              snap(ut=7660.0, time_to_soi=150.0),
              snap(ut=7700.0, time_to_soi=110.0),
              snap(ut=7786.0, time_to_soi=24.0),
              snap(ut=7800.0, time_to_soi=10.0),
              snap(ut=7810.0, body="Kerbin", time_to_soi=float("nan")),
              snap(ut=7841.0, body="Kerbin", time_to_soi=float("nan"))]
        return f

    def test_the_formation_flight_is_mission_ok(self):
        control = FakeMissionControl(self._frames())
        code, result = run(shell.SPEC, FORMATION, control)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        acts = [a.kind for a in control.actions]
        self.assertEqual(1, acts.count(mlib.ACTION_ACTIVATE_STAGE))
        rows = {a["name"]: a for a in result["assertions"]}
        self.assertTrue(rows["crossedInFormationAt1x"]["met"])

    def test_the_soi2_params_validate_and_select_the_formation(self):
        import hlib
        with open(SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        with open(SCHEMA, "rb") as fh:
            schema = tomllib.load(fh)
        self.assertEqual("soi1_mun_exit_burn", spec["driver"]["mission"])
        self.assertEqual("fixtures/saves/mun-park-kerbalx", spec["fixture"]["saveTemplate"])
        mp = spec["driver"]["missionParams"]
        self.assertEqual([], hlib._validate_mission_params(mp, schema))
        p = mlib.soi1_params_from_dict(mp)
        self.assertTrue(p.formation)
        # The stage must come after the 1x drop and inside the 60 s debris window with
        # the trail on top.
        self.assertLess(p.formation_stage_lead_seconds,
                        p.warp_lead_seconds - p.warp_arrival_tolerance_seconds)
        self.assertLessEqual(p.formation_max_stage_age_seconds, 60.0)
        self.assertGreater(p.formation_max_stage_age_seconds, p.formation_stage_lead_seconds)
        self.assertLess(p.formation_stage_lead_seconds + p.burn_trail_seconds,
                        p.straddle_timeout_seconds)


if __name__ == "__main__":
    unittest.main()
