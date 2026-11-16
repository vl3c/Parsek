"""Unit cells for the course-correction node search (correctionNodeSearch), B43's
third fix (2026-11-16, todo B43-VALL-TYLO-FLYBY-SUBSURFACE).

The re-flights `2026-11-16_0002` / `_0022_a2` flew every correction round to
within 2 m/s of its node, but MechJeb's node did not deliver the periapsis it was
asked for (Tylo pe per round -580 -> -327 -> -568 -> -483 -> -381 km). The fix
judges the node by KSP's own patched-conic prediction and, when it misses the
window, searches direction and dv against that prediction. The fake eval_pe here
is the hyperbolic focusing formula: an aim offset b at v_inf reaches periapsis
radius rp with b = rp * sqrt(1 + 2 mu / (rp v_inf^2)), inverted for rp.

Run:
    cd harness && python -m unittest discover -s missions/lib -q
"""

import math
import os
import sys
import tomllib
import types
import unittest

_LIB = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_LIB)
for _p in (_LIB, _MISSIONS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import mission_runner  # noqa: E402
import mlib  # noqa: E402

HARNESS_ROOT = os.path.dirname(os.path.dirname(_LIB))

MU_TYLO = 2.82528e12
R_TYLO = 600000.0
SOI_TYLO = 10856518.0
V_INF = 245.0
B0 = 1.4e6          # the stage-2 aim offset that arrives at pe ~ -580 km
WINDOW = (100000.0, 1000000.0)
CAP = 250.0
STEP = mlib.CORRECTION_SEARCH_STEP_MPS
RADIAL = (0.0, 0.0, 1.0)
ANTI_RADIAL = (0.0, 0.0, -1.0)


def pe_from_offset(b):
    """Periapsis altitude for aim offset |b| (the focusing formula inverted)."""
    v2 = V_INF * V_INF
    rp = (-MU_TYLO + math.sqrt(MU_TYLO * MU_TYLO + v2 * v2 * b * b)) / v2
    return rp - R_TYLO


def offset_for_pe(pe):
    rp = R_TYLO + pe
    return rp * math.sqrt(1.0 + 2.0 * MU_TYLO / (rp * V_INF * V_INF))


class FakeEncounter:
    """b = b0 + lever * dv * (radial, normal) in the B-plane; prograde moves the
    arrival time only. A miss (|b| beyond the SOI) reads None."""

    def __init__(self, lever, b0=(B0, 0.0)):
        self.lever = lever
        self.b0 = b0
        self.calls = []

    def __call__(self, direction, dv):
        self.calls.append((tuple(direction), dv))
        bx = self.b0[0] + self.lever * dv * direction[2]
        by = self.b0[1] + self.lever * dv * direction[1]
        b = math.hypot(bx, by)
        if b > SOI_TYLO:
            return None
        return pe_from_offset(b)


def _search(fake, cap=CAP, directions=mlib.CORRECTION_SEARCH_DIRECTIONS, **kw):
    return mlib.search_correction_node(fake, directions, cap, STEP, WINDOW, **kw)


class FocusingModelTests(unittest.TestCase):
    def test_the_stage_2_aim_reproduces_the_flown_arrival(self):
        self.assertAlmostEqual(pe_from_offset(B0), -580000.0, delta=5000.0)

    def test_the_surface_and_500_km_offsets_match_the_diagnosis(self):
        self.assertAlmostEqual(offset_for_pe(0.0), 7.54e6, delta=0.05e6)
        self.assertAlmostEqual(offset_for_pe(500000.0), 10.0e6, delta=0.3e6)
        self.assertLess(offset_for_pe(500000.0), SOI_TYLO)
        self.assertAlmostEqual(pe_from_offset(offset_for_pe(100000.0)), 100000.0,
                               delta=1.0)


class SearchCorrectionNodeTests(unittest.TestCase):
    def test_a_40000_s_lever_lands_in_the_window_under_the_cap(self):
        fake = FakeEncounter(lever=40000.0)
        r = _search(fake)
        self.assertIsNotNone(r.direction, r.reason)
        self.assertEqual(r.direction, RADIAL)
        want = (offset_for_pe(WINDOW[0]) - B0) / 40000.0   # ~169 m/s
        self.assertLess(want, CAP)
        self.assertGreaterEqual(r.dv, want - 1e-6)
        self.assertLessEqual(r.dv, want + STEP / 2 ** mlib.CORRECTION_SEARCH_BISECT_ITERS)
        self.assertGreaterEqual(r.pe, WINDOW[0])
        self.assertLessEqual(r.pe, WINDOW[1])
        self.assertLessEqual(r.evaluations, mlib.CORRECTION_SEARCH_MAX_EVALS)
        self.assertEqual(r.evaluations, len(fake.calls))

    def test_a_2000_s_lever_is_refused_over_the_cap(self):
        fake = FakeEncounter(lever=2000.0)
        r = _search(fake)
        self.assertIsNone(r.direction)
        self.assertIn("cap", r.reason)
        self.assertEqual(r.directions_tried, len(mlib.CORRECTION_SEARCH_DIRECTIONS))
        self.assertTrue(all(dv <= CAP for _d, dv in fake.calls))

    def test_a_v_shaped_curve_is_crossed_not_stopped_at_its_minimum(self):
        # Anti-radial first moves the aim point THROUGH Tylo's centre (b = 0 at
        # 35 m/s): pe falls to about -R, then rises out the other side.
        fake = FakeEncounter(lever=40000.0)
        r = _search(fake, directions=(ANTI_RADIAL,))
        self.assertIsNotNone(r.direction, r.reason)
        pes = [fake(ANTI_RADIAL, dv) for dv in (10.0, 30.0, 35.0, 40.0, 60.0)]
        self.assertLess(pes[2], pes[0])
        self.assertLess(pes[2], pes[4])
        self.assertAlmostEqual(pes[2], -R_TYLO, delta=1.0)
        want = (offset_for_pe(WINDOW[0]) + B0) / 40000.0   # ~239 m/s
        self.assertAlmostEqual(r.dv, want, delta=STEP)
        self.assertGreaterEqual(r.pe, WINDOW[0])

    def test_no_eval_asks_above_the_cap(self):
        fake = FakeEncounter(lever=40000.0)
        r = _search(fake, cap=150.0)
        self.assertIsNone(r.direction)
        self.assertTrue(fake.calls)
        self.assertTrue(all(dv <= 150.0 for _d, dv in fake.calls))
        self.assertIn(150.0, [dv for _d, dv in fake.calls])

    def test_the_last_step_is_clamped_to_an_off_grid_cap(self):
        fake = FakeEncounter(lever=2000.0)
        mlib.search_correction_node(fake, (RADIAL,), 255.0, STEP, WINDOW)
        self.assertEqual(max(dv for _d, dv in fake.calls), 255.0)

    def test_the_evaluation_budget_bounds_the_search(self):
        fake = FakeEncounter(lever=40000.0)
        r = _search(fake, max_evals=5)
        self.assertIsNone(r.direction)
        self.assertEqual(len(fake.calls), 5)
        self.assertIn("budget", r.reason)

    def test_a_candidate_bounds_the_later_directions(self):
        fake = FakeEncounter(lever=40000.0)
        r = _search(fake)
        later = [dv for d, dv in fake.calls if d != RADIAL]
        self.assertTrue(later)
        self.assertTrue(all(dv < r.dv + STEP for dv in later))

    def test_it_is_deterministic_and_ties_go_to_the_earlier_direction(self):
        # Aimed dead centre, +radial and -radial are symmetric.
        a = _search(FakeEncounter(lever=40000.0, b0=(0.0, 0.0)))
        b = _search(FakeEncounter(lever=40000.0, b0=(0.0, 0.0)))
        self.assertEqual(a, b)
        self.assertEqual(a.direction, RADIAL)

    def test_a_miss_or_nan_reads_out_of_window(self):
        calls = []

        def miss(direction, dv):
            calls.append(dv)
            return None if dv < 50.0 else (float("nan") if dv < 100.0 else 200000.0)

        r = mlib.search_correction_node(miss, (RADIAL,), CAP, STEP, WINDOW)
        self.assertGreaterEqual(r.dv, 90.0)
        self.assertLessEqual(r.dv, 100.0)
        self.assertEqual(r.pe, 200000.0)

    def test_unusable_inputs_refuse_without_evaluating(self):
        fake = FakeEncounter(lever=40000.0)
        for kw in (dict(cap_mps=0.0), dict(step_mps=0.0),
                   dict(pe_window=(1.0, 0.0)), dict(directions=())):
            args = dict(eval_pe=fake, directions=mlib.CORRECTION_SEARCH_DIRECTIONS,
                        cap_mps=CAP, step_mps=STEP, pe_window=WINDOW)
            args.update(kw)
            with self.subTest(kw=kw):
                r = mlib.search_correction_node(**args)
                self.assertIsNone(r.direction)
                self.assertTrue(r.reason)
        self.assertEqual(fake.calls, [])


class SearchDecisionTests(unittest.TestCase):
    def _d(self, raw_body="Tylo", raw_pe=-580000.0, mj_dv=85.6, mj_body="Tylo",
           mj_pe=-327000.0, cap=CAP):
        return mlib.correction_search_decision(raw_body, raw_pe, mj_dv, mj_body, mj_pe,
                                               "Tylo", WINDOW, cap, 0.5)[0]

    def test_the_flown_round_0_plan_is_refined(self):
        self.assertEqual(self._d(), mlib.CC_SEARCH_REFINE)

    def test_an_in_window_mechjeb_node_is_kept(self):
        self.assertEqual(self._d(mj_pe=150000.0), mlib.CC_SEARCH_KEEP)

    def test_an_in_window_raw_trajectory_removes_the_node(self):
        self.assertEqual(self._d(raw_pe=120000.0, mj_pe=-300000.0),
                         mlib.CC_SEARCH_ALREADY_GOOD)

    def test_an_in_window_node_over_the_cap_is_refined(self):
        self.assertEqual(self._d(mj_pe=150000.0, mj_dv=265.5), mlib.CC_SEARCH_REFINE)

    def test_an_in_window_negligible_node_is_removed(self):
        self.assertEqual(self._d(mj_pe=150000.0, mj_dv=0.2), mlib.CC_SEARCH_NEGLIGIBLE)

    def test_another_body_no_encounter_or_no_node_is_refined(self):
        self.assertEqual(self._d(mj_body="Jool", mj_pe=150000.0), mlib.CC_SEARCH_REFINE)
        self.assertEqual(self._d(mj_body="", mj_pe=float("nan")), mlib.CC_SEARCH_REFINE)
        self.assertEqual(self._d(mj_dv=float("nan"), mj_body="", mj_pe=float("nan")),
                         mlib.CC_SEARCH_REFINE)
        self.assertEqual(self._d(raw_body="Jool", raw_pe=150000.0),
                         mlib.CC_SEARCH_REFINE)


class NodeUtTests(unittest.TestCase):
    def test_the_node_sits_one_lead_ahead(self):
        self.assertEqual(mlib.correction_search_node_ut(1000.0, 900.0, 70000.0),
                         (1900.0, ""))

    def test_an_unread_soi_clock_does_not_refuse(self):
        self.assertEqual(mlib.correction_search_node_ut(1000.0, 900.0, float("nan"))[0],
                         1900.0)

    def test_too_close_to_the_soi_or_an_unread_clock_refuses(self):
        self.assertIsNone(mlib.correction_search_node_ut(1000.0, 900.0, 1700.0)[0])
        self.assertIsNone(mlib.correction_search_node_ut(float("nan"), 900.0, 70000.0)[0])
        self.assertIsNone(mlib.correction_search_node_ut(1000.0, 0.0, 70000.0)[0])


class RefinedReadBackTests(unittest.TestCase):
    def _v(self, dv=169.0, body="Tylo", pe=101000.0, cap=CAP):
        return mlib.classify_refined_correction(dv, body, pe, "Tylo", WINDOW, cap, 0.5)[0]

    def test_an_in_window_node_under_the_cap_flies(self):
        self.assertEqual(self._v(), mlib.PLAN_FLY)

    def test_the_cap_bounds_the_refined_dv(self):
        self.assertEqual(self._v(dv=250.5), mlib.PLAN_OVER_CAP)
        self.assertEqual(self._v(dv=float("nan")), mlib.PLAN_OVER_CAP)

    def test_a_subsurface_or_foreign_read_back_is_not_flown(self):
        self.assertEqual(self._v(pe=-327000.0), "out-of-window")
        self.assertEqual(self._v(body="Jool"), "out-of-window")
        self.assertEqual(self._v(pe=float("nan")), "out-of-window")

    def test_a_negligible_refined_node_is_removed(self):
        self.assertEqual(self._v(dv=0.3), mlib.PLAN_NEGLIGIBLE)


def _b43_params():
    with open(os.path.join(HARNESS_ROOT, "scenarios", "B43-vall-tylo-transfer.toml"),
              "rb") as fh:
        return tomllib.load(fh)["driver"]["missionParams"]


class SearchParamTests(unittest.TestCase):
    def test_off_by_default_and_the_plan_action_is_unchanged(self):
        p = mlib.b5_params_from_dict({})
        self.assertFalse(p.correction_node_search)
        action = mlib._b5_course_correct_action(p)
        self.assertIsNone(action.correction_search)
        self.assertEqual(action, mlib.Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT,
                                             p.course_correct_periapsis,
                                             limit=p.max_correction_dv))

    def test_b43_arms_the_search_and_the_action_carries_it(self):
        p = mlib.b5_params_from_dict(_b43_params())
        self.assertTrue(p.correction_node_search)
        self.assertEqual(p.correction_node_search_pe_window, WINDOW)
        self.assertEqual(p.course_correct_periapsis, 150000.0)
        self.assertEqual(p.max_correction_dv, CAP)
        self.assertGreaterEqual(WINDOW[0], p.target_periapsis_floor)
        self.assertTrue(WINDOW[0] <= p.course_correct_periapsis <= WINDOW[1])
        action = mlib._b5_course_correct_action(p)
        self.assertEqual(action.correction_search,
                         ("Tylo", 100000.0, 1000000.0, 900.0))

    def test_b43s_round_0_lever_reaches_the_window_under_its_cap(self):
        # Round 0 triggers at 70,000 s to SOI; the node sits one lead later, and
        # the encounter's periapsis is another ~10,000 s on.
        p = mlib.b5_params_from_dict(_b43_params())
        lever = p.correction_trigger_time_to_soi[0] - p.correction_node_lead
        r = mlib.search_correction_node(FakeEncounter(lever=lever),
                                        mlib.CORRECTION_SEARCH_DIRECTIONS,
                                        p.max_correction_dv, STEP,
                                        p.correction_node_search_pe_window)
        self.assertIsNotNone(r.direction, r.reason)
        self.assertLess(r.dv, 0.5 * p.max_correction_dv)

    def test_load_refuses_the_inconsistent_shapes(self):
        base = dict(courseCorrectPeriapsisMeters=150000,
                    targetPeriapsisFloorMeters=40000, targetBodyName="Tylo",
                    correctionNodeSearch=True,
                    correctionNodeSearchPeWindowMeters={"min": 100000, "max": 1000000})
        self.assertTrue(mlib.b5_params_from_dict(dict(base)).correction_node_search)
        bad = [
            dict(correctionNodeSearch=False),                              # inert window
            dict(correctionNodeSearchPeWindowMeters=None),                 # no window
            dict(correctionNodeSearchPeWindowMeters={"min": 20000, "max": 1000000}),
            dict(correctionNodeSearchPeWindowMeters={"min": 500000, "max": 400000}),
            dict(correctionNodeLeadSeconds=0),
            dict(correctionNodeSearch="yes"),
            dict(courseCorrectPeriapsisMeters=0),
        ]
        for change in bad:
            spec = dict(base)
            spec.update(change)
            if spec.get("correctionNodeSearchPeWindowMeters") is None:
                spec.pop("correctionNodeSearchPeWindowMeters", None)
            with self.subTest(change=change):
                with self.assertRaises(ValueError):
                    mlib.b5_params_from_dict(spec)

    def test_load_refuses_a_window_the_elliptical_capture_cannot_close(self):
        spec = _b43_params()
        spec = dict(spec)
        spec["correctionNodeSearchPeWindowMeters"] = {"min": 100000, "max": 6000000}
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(spec)
        spec["correctionNodeSearchPeWindowMeters"] = {"min": 40000, "max": 1000000}
        spec["parkMaxEccentricity"] = 0.8
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(spec)


# ---------------------------------------------------------------------------
# The runner shell over fake kRPC objects: the search runs against a node whose
# predicted encounter is the focusing model above.

def _orbit(body, pe):
    nxt = None if body is None else types.SimpleNamespace(
        body=types.SimpleNamespace(name=body), periapsis_altitude=pe)
    return types.SimpleNamespace(next_orbit=nxt, time_to_soi_change=70000.0)


class _FakeNode:
    def __init__(self, control, ut, prograde=0.0, normal=0.0, radial=0.0):
        self._control = control
        self.ut = ut
        self.prograde, self.normal, self.radial = prograde, normal, radial

    @property
    def delta_v(self):
        return math.sqrt(self.prograde ** 2 + self.normal ** 2 + self.radial ** 2)

    @property
    def orbit(self):
        dv = self.delta_v
        if dv == 0.0:
            return _orbit("Tylo", self._control.encounter((0.0, 0.0, 1.0), 0.0))
        d = (self.prograde / dv, self.normal / dv, self.radial / dv)
        pe = self._control.encounter(d, dv)
        return _orbit(None if pe is None else "Tylo", pe)

    def remove(self):
        self._control.nodes.remove(self)


class _FakeControl:
    def __init__(self, encounter):
        self.encounter = encounter
        self.nodes = []

    def add_node(self, ut, prograde=0.0, normal=0.0, radial=0.0):
        node = _FakeNode(self, ut, prograde, normal, radial)
        self.nodes.append(node)
        return node

    def remove_nodes(self):
        self.nodes[:] = []


class _FakePlanner:
    """MechJeb's course correction: one node with the flown round-0 dv and a
    fixed prediction (the -327 km it delivered, or an in-window one)."""

    def __init__(self, control, dv, pe):
        self.control, self.dv, self.pe = control, dv, pe
        self.course_correct_final_pe_a = None

    def make_nodes(self):
        node = types.SimpleNamespace(delta_v=self.dv, orbit=_orbit("Tylo", self.pe))
        self.control.nodes.append(node)
        return [node]


def _runner(lever, mj_dv=85.6, mj_pe=-327000.0, raw_pe=-580000.0):
    control = _FakeControl(FakeEncounter(lever=lever))
    vessel = types.SimpleNamespace(control=control, orbit=_orbit("Tylo", raw_pe))
    planner = _FakePlanner(control, mj_dv, mj_pe)
    c = mission_runner.KrpcMissionControl(use_mechjeb=True)
    c._mechjeb = types.SimpleNamespace(maneuver_planner=types.SimpleNamespace(
        operation_course_correction=planner))
    c._conn = types.SimpleNamespace(space_center=types.SimpleNamespace(
        active_vessel=vessel, ut=1000.0))
    return c, control


SEARCH_ACTION = mlib.Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT, 150000.0, limit=CAP,
                            correction_search=("Tylo", WINDOW[0], WINDOW[1], 900.0))


class RunnerShellTests(unittest.TestCase):
    def setUp(self):
        self.lines = []
        self._orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = self.lines.append

    def tearDown(self):
        mission_runner._stdout_sink = self._orig

    def _has(self, text):
        return any(text in line for line in self.lines)

    def test_the_flown_round_0_plan_is_refined_into_the_window(self):
        c, control = _runner(lever=40000.0)
        c.perform(SEARCH_ACTION)
        self.assertEqual(len(control.nodes), 1)
        node = control.nodes[0]
        self.assertEqual(node.ut, 1900.0)
        self.assertAlmostEqual(node.radial, (offset_for_pe(WINDOW[0]) - B0) / 40000.0,
                               delta=STEP / 2 ** mlib.CORRECTION_SEARCH_BISECT_ITERS)
        self.assertTrue(self._has("predicts nextBody=Tylo nextPe=-327000.000"))
        self.assertTrue(self._has("refined plan accepted"))

    def test_an_in_window_mechjeb_node_is_kept(self):
        c, control = _runner(lever=40000.0, mj_pe=150000.0)
        c.perform(SEARCH_ACTION)
        self.assertEqual(len(control.nodes), 1)
        self.assertEqual(control.nodes[0].delta_v, 85.6)
        self.assertTrue(self._has("plan accepted (MechJeb node"))

    def test_a_short_lever_leaves_no_node_to_fly(self):
        c, control = _runner(lever=2000.0)
        c.perform(SEARCH_ACTION)
        self.assertEqual(control.nodes, [])
        self.assertTrue(self._has("node search found no node"))

    def test_an_in_window_raw_trajectory_burns_nothing(self):
        c, control = _runner(lever=40000.0, raw_pe=300000.0)
        c.perform(SEARCH_ACTION)
        self.assertEqual(control.nodes, [])
        self.assertTrue(self._has("trajectory already good"))


if __name__ == "__main__":
    unittest.main()
