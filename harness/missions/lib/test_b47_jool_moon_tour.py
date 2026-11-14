"""Unit cells for mission `b47_jool_moon_tour` and its composed machine (mlib
`mtour_*`), the Jool multi-moon tour forge (todo NEXT-TESTS-TO-BUILD-2026-11-11
item 1).

The mission's claim is "b5_decide verbatim, once per leg, with the mid-mission
commit dropped on every park but the last". Each class pins one part:

  1. THE PARAMS: the committed spec loads, its legs are B38's / B26's tables with
     only the documented keys moved, and every malformed tour is refused by name.
  2. THE HAND-OFF CHECK: a park band the next entry gate does not contain is a
     load error, one fault per bound.
  3. THE COMPOSER over a stubbed b5_decide (so the composer's own edges are what
     is under test): the commit drop, the fresh next-leg machine, the leg-tagged
     flake / loss, the final commit kept, idempotence.
  4. THE REAL DOOR: leg 2's own b5 machine accepts the worst park leg 1 admits.
  5. THE ROWS: per-leg prefixes, the non-final commit row dropped, `tourCompleted`.
  6. THE SIZING: the leg-2 / leg-3 escape and elliptical-capture nodes computed by
     mlib's own planners at the spec's numbers.
  7. THE SHELL, THE SCHEMA, the flight-efficiency machine map and hlib's spec gate.

NO krpc, NO KSP, NO network. ASCII only.
"""

import dataclasses
import math
import os
import sys
import tomllib
import unittest
from unittest import mock

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
for _p in (_HERE, _MISSIONS, os.path.join(_HARNESS, "lib")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import mlib                        # noqa: E402
import b47_jool_moon_tour          # noqa: E402
from mlib import Action            # noqa: E402

SPEC = "B47-jool-moon-tour"


def _spec(name=SPEC):
    with open(os.path.join(_HARNESS, "scenarios", name + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _legs():
    return [dict(leg) for leg in _spec()["driver"]["missionParams"]["legs"]]


def _params(legs=None):
    return mlib.mtour_params_from_dict({"legs": legs if legs is not None else _legs()})


def _source_params(name):
    return dict(_spec(name)["driver"]["missionParams"])


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


# --------------------------------------------------------------------------------
# 1. THE PARAMS
# --------------------------------------------------------------------------------
class SpecParamTests(unittest.TestCase):

    def test_the_committed_spec_loads_as_three_chained_legs(self):
        p = _params()
        self.assertEqual([("Jool", "Laythe"), ("Laythe", "Vall"), ("Vall", "Tylo")],
                         [(leg.home_body, leg.target_body) for leg in p.legs])
        self.assertTrue(all(leg.capture_enabled and leg.start_in_orbit for leg in p.legs))
        self.assertFalse(p.legs[0].parent_relay_transfer)
        self.assertTrue(p.legs[1].parent_relay_transfer)
        self.assertTrue(p.legs[2].parent_relay_transfer)

    def test_leg1_is_b38_with_only_the_park_band_tightened(self):
        src = _source_params("B38-jool-laythe-continuation")
        leg = _legs()[0]
        moved = {k for k in set(src) | set(leg) if src.get(k) != leg.get(k)}
        self.assertEqual({"parkMaxApoapsisMeters", "parkMaxEccentricity"}, moved)
        self.assertEqual(300000, leg["parkMaxApoapsisMeters"])
        self.assertEqual(0.10, leg["parkMaxEccentricity"])

    def test_leg2_is_b26_with_only_the_elliptical_capture_added(self):
        src = _source_params("B26-laythe-vall-transfer")
        leg = _legs()[1]
        moved = {k for k in set(src) | set(leg) if src.get(k) != leg.get(k)}
        self.assertEqual({"captureEllipticalApoapsisMeters", "parkMaxApoapsisMeters",
                          "parkMaxEccentricity"}, moved)

    def test_leg3_is_leg2_with_the_documented_vall_to_tylo_keys(self):
        leg2, leg3 = _legs()[1], _legs()[2]
        moved = {k for k in set(leg2) | set(leg3) if leg2.get(k) != leg3.get(k)}
        self.assertEqual({
            "startInOrbitMinPeriapsisMeters", "startInOrbitMaxApoapsisMeters",
            "startInOrbitMaxEccentricity", "homeBodyName", "targetBodyName",
            "escapeSoiSpeedMps", "escapeMaxDeltaVMps", "escapeTimeoutSeconds",
            "transferMinApoapsisMeters", "courseCorrectPeriapsisMeters",
            "correctionTriggerTimeToSoiSeconds", "coastTimeoutSeconds",
            "targetPeriapsisFloorMeters", "captureEllipticalApoapsisMeters",
            "parkMinPeriapsisMeters", "parkMaxApoapsisMeters", "parkMaxEccentricity"},
            moved)

    def test_every_leg_hands_off_cleanly(self):
        p = _params()
        for i in range(1, len(p.legs)):
            self.assertEqual([], mlib.mtour_leg_handoff_faults(p.legs[i - 1], p.legs[i]), i)

    def _refused(self, legs, needle):
        with self.assertRaises(ValueError) as cm:
            _params(legs)
        self.assertIn(needle, str(cm.exception))

    def test_a_tour_needs_two_leg_tables(self):
        with self.assertRaises(ValueError):
            mlib.mtour_params_from_dict({})
        self._refused(_legs()[:1], "at least 2")
        self._refused([_legs()[0], "Vall"], "legs[1] must be a table")

    def test_a_bad_leg_is_named(self):
        legs = _legs()
        legs[1]["captureEllipticalApoapsisMeters"] = 2_000_000   # above its park ceiling
        self._refused(legs, "legs[1]: captureEllipticalApoapsisMeters")

    def test_every_leg_must_be_an_orbit_start_park(self):
        legs = _legs()
        legs[2]["captureEnabled"] = False
        legs[2].pop("captureEllipticalApoapsisMeters")
        self._refused(legs, "captureEnabled is required")
        legs = _legs()
        legs[1]["startInOrbit"] = False
        self._refused(legs, "startInOrbit is required")
        legs = _legs()
        legs[0]["landingEnabled"] = True
        with self.assertRaises(ValueError):
            _params(legs)

    def test_targets_may_not_repeat(self):
        legs = _legs()[:2] + [dict(_legs()[1], homeBodyName="Vall", targetBodyName="Laythe",
                                   startInOrbitMinPeriapsisMeters=20000,
                                   startInOrbitMaxApoapsisMeters=1600000,
                                   startInOrbitMaxEccentricity=0.65)]
        with self.assertRaises(ValueError):
            _params(legs)


# --------------------------------------------------------------------------------
# 2. THE HAND-OFF CHECK
# --------------------------------------------------------------------------------
class HandoffFaultTests(unittest.TestCase):

    def _pair(self, **nxt_over):
        legs = _legs()
        prev = mlib.b5_params_from_dict(legs[0])
        nxt = dataclasses.replace(mlib.b5_params_from_dict(legs[1]), **nxt_over)
        return mlib.mtour_leg_handoff_faults(prev, nxt)

    def test_the_spec_pair_is_clean(self):
        self.assertEqual([], self._pair())

    def test_each_bound_is_its_own_fault(self):
        self.assertIn("periapsis floor", " ".join(self._pair(start_in_orbit_min_periapsis=60000)))
        self.assertIn("apoapsis ceiling", " ".join(self._pair(start_in_orbit_max_apoapsis=200000)))
        self.assertIn("eccentricity ceiling",
                      " ".join(self._pair(start_in_orbit_max_eccentricity=0.05)))
        self.assertIn("park situations",
                      " ".join(self._pair(start_in_orbit_situations=("SUB_ORBITAL",))))
        self.assertIn("departs", " ".join(self._pair(home_body="Tylo")))
        self.assertIn("startInOrbit", " ".join(self._pair(start_in_orbit=False)))

    def test_an_unbounded_entry_ceiling_contains_any_park(self):
        self.assertEqual([], self._pair(start_in_orbit_max_apoapsis=0.0))

    def test_the_spec_load_reports_the_fault(self):
        legs = _legs()
        legs[1]["startInOrbitMaxApoapsisMeters"] = 200000
        with self.assertRaises(ValueError) as cm:
            _params(legs)
        self.assertIn("legs[0] -> legs[1] hand-off", str(cm.exception))


# --------------------------------------------------------------------------------
# 3. THE COMPOSER over a stubbed b5_decide
# --------------------------------------------------------------------------------
COMMIT = Action(mlib.ACTION_PARSEK_COMMIT_TREE)
TEARDOWN = Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0) if hasattr(
    mlib, "ACTION_SET_PHYSICS_WARP") else Action("teardown")


class _Stub:
    """Replace b5_decide with a scripted transition: each call returns the next
    (state-update dict, actions) pair applied to the incoming leg state."""

    def __init__(self, script):
        self.script = list(script)

    def __call__(self, state, snapshot):
        upd, actions = self.script.pop(0)
        reached = state.phases_reached
        if "phase" in upd and upd["phase"] not in reached:
            reached = reached + (upd["phase"],)
        return dataclasses.replace(state, phases_reached=reached, **upd), list(actions)


class ComposerTests(unittest.TestCase):

    def _run(self, script, state=None, ut=100.0):
        st = state or mlib.mtour_initial_state(_params())
        out = []
        with mock.patch.object(mlib, "b5_decide", _Stub(script)):
            for k in range(len(script)):
                st, acts = mlib.mtour_decide(st, snap(ut=ut + k))
                out.append(acts)
        return st, out

    def test_a_non_final_park_exit_drops_the_commit_and_starts_the_next_leg(self):
        st, out = self._run([
            ({"phase": mlib.B5_PARK}, []),
            ({"phase": mlib.B5_ORBIT_COMMIT, "phase_entry_ut": 101.0}, [TEARDOWN, COMMIT]),
        ])
        self.assertEqual([TEARDOWN], out[1])
        self.assertEqual(1, st.leg_index)
        self.assertEqual(1, len(st.finished_legs))
        self.assertEqual(mlib.B5_ORBIT_COMMIT, st.finished_legs[0].phase)
        self.assertEqual("Vall", st.flight.params.target_body)
        self.assertEqual(mlib.B5_PRELAUNCH, st.flight.phase)
        self.assertEqual((101.0,), st.leg_exit_uts)
        self.assertEqual(mlib.B5_PRELAUNCH, st.phase)
        self.assertIn(mlib.B5_ORBIT_COMMIT, st.phases_reached)
        self.assertFalse(st.done)
        self.assertIsNone(st.warp_to_cmd)

    def test_the_final_leg_keeps_its_commit_and_finishes_clean(self):
        p = _params()
        st = dataclasses.replace(mlib.mtour_initial_state(p), leg_index=2,
                                 flight=mlib.b5_initial_state(p.legs[2]))
        st, out = self._run([
            ({"phase": mlib.B5_ORBIT_COMMIT}, [COMMIT]),
            ({"phase": mlib.B5_ORBIT_COMMITTED, "done": True}, []),
        ], state=st)
        self.assertEqual([COMMIT], out[0])
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertIn(mlib.B5_ORBIT_COMMITTED, st.phases_reached)

    def test_a_leg_loss_is_an_assert_fail_naming_the_leg(self):
        st, _ = self._run([({"done": True, "verdict": mlib.MISSION_ASSERT_FAIL,
                             "loss_reason": "left the SOI"}, [])])
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("leg 1/3 (Jool->Laythe): left the SOI", st.loss_reason)

    def test_a_leg_flake_is_a_flake_naming_the_leg(self):
        st, _ = self._run([({"done": True, "verdict": mlib.MISSION_FLAKE,
                             "phase": mlib.B5_PLAN_TRANSFER,
                             "flake_reason": "no node"}, [])])
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.B5_PLAN_TRANSFER, st.flake_phase)
        self.assertIn("leg 1/3 (Jool->Laythe)", st.flake_reason)
        self.assertIn("no node", st.flake_reason)

    def test_a_clean_done_before_a_non_final_park_exit_is_a_flake(self):
        st, _ = self._run([({"done": True, "phase": mlib.B5_ORBIT_COMMITTED}, [])])
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("before its PARK exit", st.flake_reason)

    def test_done_is_idempotent(self):
        st, _ = self._run([({"done": True, "verdict": mlib.MISSION_FLAKE}, [])])
        again, acts = mlib.mtour_decide(st, snap(ut=500.0))
        self.assertIs(st, again)
        self.assertEqual([], acts)

    def test_the_frozen_detector_finds_the_active_leg(self):
        st = mlib.mtour_initial_state(_params())
        self.assertIsInstance(st.flight, mlib.B5State)


# --------------------------------------------------------------------------------
# 4. THE REAL DOOR: leg 2's b5 machine accepts the worst park leg 1 admits
# --------------------------------------------------------------------------------
class RealHandoffTests(unittest.TestCase):

    def _door(self, leg_index, body, ap, pe, ecc):
        p = _params()
        st = dataclasses.replace(mlib.mtour_initial_state(p), leg_index=leg_index,
                                 flight=mlib.b5_initial_state(p.legs[leg_index]))
        for k in range(p.legs[leg_index].start_in_orbit_debounce + 1):
            st, _ = mlib.mtour_decide(st, snap(ut=1000.0 + k, body=body, apoapsis=ap,
                                               periapsis=pe, eccentricity=ecc,
                                               situation="ORBITING"))
        return st

    def test_leg2_enters_orbit_from_leg1s_worst_park(self):
        leg1 = _params().legs[0]
        st = self._door(1, "Laythe", leg1.park_max_apoapsis, leg1.park_min_periapsis,
                        leg1.park_max_eccentricity)
        self.assertIn(mlib.B5_ORBIT, st.flight.phases_reached)
        self.assertFalse(st.done)

    def test_leg3_enters_orbit_from_leg2s_worst_park(self):
        leg2 = _params().legs[1]
        st = self._door(2, "Vall", leg2.park_max_apoapsis, leg2.park_min_periapsis,
                        leg2.park_max_eccentricity)
        self.assertIn(mlib.B5_ORBIT, st.flight.phases_reached)

    def test_leg3_refuses_a_park_past_the_band(self):
        leg2 = _params().legs[1]
        st = self._door(2, "Vall", leg2.park_max_apoapsis * 1.2, leg2.park_min_periapsis,
                        leg2.park_max_eccentricity)
        self.assertNotIn(mlib.B5_ORBIT, st.flight.phases_reached)


# --------------------------------------------------------------------------------
# 5. THE ROWS
# --------------------------------------------------------------------------------
class RowTests(unittest.TestCase):

    def test_an_unstarted_tour_names_how_far_it_got(self):
        p = _params()
        rows = {r.name: r for r in mlib.evaluate_mtour_assertions([], p, mlib.mtour_initial_state(p))}
        self.assertIn("leg1Laythe.reachedOrbit", rows)
        self.assertNotIn("leg1Laythe.treeCommitted", rows)
        self.assertFalse(rows["leg2Vall.started"].met)
        self.assertFalse(rows["leg3Tylo.started"].met)
        self.assertFalse(rows["tourCompleted"].met)

    def test_a_finished_tour_reports_every_leg_and_completes(self):
        p = _params()
        done_legs = tuple(dataclasses.replace(
            mlib.b5_initial_state(leg),
            phases_reached=(mlib.B5_PRELAUNCH, mlib.B5_ORBIT, mlib.B5_PARK,
                            mlib.B5_ORBIT_COMMIT))
            for leg in p.legs[:2])
        final = dataclasses.replace(
            mlib.b5_initial_state(p.legs[2]),
            phases_reached=(mlib.B5_PRELAUNCH, mlib.B5_ORBIT, mlib.B5_ORBIT_COMMIT,
                            mlib.B5_ORBIT_COMMITTED), done=True)
        st = dataclasses.replace(mlib.mtour_initial_state(p), leg_index=2, flight=final,
                                 finished_legs=done_legs, leg_exit_uts=(1.0, 2.0))
        rows = {r.name: r for r in mlib.evaluate_mtour_assertions([], p, st)}
        self.assertTrue(rows["tourCompleted"].met)
        self.assertEqual(3, rows["tourCompleted"].value)
        self.assertIn("leg2Vall.escapedHomeSoi", rows)
        self.assertIn("leg3Tylo.escapedHomeSoi", rows)
        self.assertNotIn("leg1Laythe.escapedHomeSoi", rows)
        self.assertIn("leg3Tylo.treeCommitted", rows)
        self.assertNotIn("leg2Vall.treeCommitted", rows)
        for row in rows.values():
            row.to_dict()                               # serialises (NaN scrubbed)

    def test_a_tour_without_the_final_commit_is_not_complete(self):
        p = _params()
        st = dataclasses.replace(mlib.mtour_initial_state(p), leg_index=2,
                                 flight=mlib.b5_initial_state(p.legs[2]))
        rows = {r.name: r for r in mlib.evaluate_mtour_assertions([], p, st)}
        self.assertFalse(rows["tourCompleted"].met)


# --------------------------------------------------------------------------------
# 6. THE SIZING, through mlib's own node planners at the spec's numbers
# --------------------------------------------------------------------------------
DV_AT_LAYTHE_PARK = 2484.0     # measured off laythe-park-nerv / jool-laythe-recorded


def _hyperbolic_ap(body, pe_alt, v_inf):
    mu, radius, _soi = mlib._body_gravity(body)
    sma = -mu / (v_inf ** 2)
    return 2.0 * sma - (radius + pe_alt) - radius


class SizingTests(unittest.TestCase):

    def test_the_vall_escape_from_leg2s_capture_ellipse_is_cheap(self):
        leg2, leg3 = _params().legs[1], _params().legs[2]
        plan = mlib.escape_node_plan(leg3, snap(
            ut=1000.0, body="Vall", periapsis=leg2.course_correct_periapsis,
            apoapsis=leg2.capture_elliptical_apoapsis, eccentricity=0.4,
            time_to_periapsis=2000.0))
        self.assertEqual("", plan.reason)
        self.assertLess(plan.dv, 100.0)
        self.assertLess(plan.dv, leg3.escape_max_dv)

    def test_the_vall_escape_survives_the_worst_admitted_park(self):
        leg2, leg3 = _params().legs[1], _params().legs[2]
        plan = mlib.escape_node_plan(leg3, snap(
            ut=1000.0, body="Vall", periapsis=leg2.park_min_periapsis,
            apoapsis=leg2.park_max_apoapsis, eccentricity=leg2.park_max_eccentricity,
            time_to_periapsis=2000.0))
        self.assertEqual("", plan.reason)
        self.assertLess(plan.dv, leg3.escape_max_dv)

    def test_the_tylo_stage2_floor_sits_under_tylos_orbit(self):
        _mu, jool_r, _soi = mlib._body_gravity("Jool")
        tylo_alt = 68_500_000.0 - jool_r
        self.assertLess(_params().legs[2].transfer_min_apoapsis, tylo_alt)
        self.assertGreater(_params().legs[2].transfer_min_apoapsis, 43_152_000.0 - jool_r)

    def test_the_elliptical_captures_close_and_are_cheaper_than_circular(self):
        for idx, body, v_inf in ((1, "Vall", 650.0), (2, "Tylo", 245.0)):
            leg = _params().legs[idx]
            pe = leg.course_correct_periapsis
            plan = mlib.capture_node_plan(leg, snap(
                ut=5000.0, body=body, periapsis=pe,
                apoapsis=_hyperbolic_ap(body, pe, v_inf), time_to_periapsis=300.0))
            self.assertEqual("", plan.reason, body)
            self.assertLess(plan.dv, 0.0, body)
            mu, radius, _soi = mlib._body_gravity(body)
            r = radius + pe
            circular = math.sqrt(v_inf ** 2 + 2 * mu / r) - math.sqrt(mu / r)
            self.assertLess(-plan.dv, circular, body)

    def test_the_nominal_tour_fits_the_tank(self):
        # Leg 2: B26 flight 3 measured escape 586.7 + stage 2 415.5 + corrections
        # 75.4, plus the elliptical Vall capture at its measured 168,783 m arrival.
        leg2, leg3 = _params().legs[1], _params().legs[2]
        mu_v, r_v, _ = mlib._body_gravity("Vall")
        rp = r_v + 168_783.0
        v_hyp = 315.07 + math.sqrt(mu_v / rp)          # B26's measured circular capture
        ra = r_v + leg2.capture_elliptical_apoapsis
        vall_capture = v_hyp - math.sqrt(mu_v * (2 / rp - 2 / (rp + ra)))
        leg2_dv = 586.7 + 415.5 + 75.4 + vall_capture
        # Leg 3: the escape node, stage 2 bounded by 2 x the SOI speed, two
        # corrections at the cap, the Tylo elliptical capture at the request.
        esc = mlib.escape_node_plan(leg3, snap(
            ut=0.0, body="Vall", periapsis=168_783.0,
            apoapsis=leg2.capture_elliptical_apoapsis, time_to_periapsis=1000.0)).dv
        cap = -mlib.capture_node_plan(leg3, snap(
            ut=0.0, body="Tylo", periapsis=leg3.course_correct_periapsis,
            apoapsis=_hyperbolic_ap("Tylo", leg3.course_correct_periapsis, 245.0),
            time_to_periapsis=300.0)).dv
        leg3_nominal = esc + leg3.escape_soi_speed + 75.0 + cap
        leg3_worst = esc + 2 * leg3.escape_soi_speed + 2 * leg3.max_correction_dv + cap
        self.assertLess(leg2_dv + leg3_nominal, DV_AT_LAYTHE_PARK - 400.0)
        # The worst case on every band is NOT covered; the first flight measures it.
        self.assertGreater(leg2_dv + leg3_worst, DV_AT_LAYTHE_PARK - 600.0)


# --------------------------------------------------------------------------------
# 7. THE SHELL, THE SCHEMA, THE MAPS, THE SPEC GATE
# --------------------------------------------------------------------------------
class ShellTests(unittest.TestCase):

    def test_the_shell_delegates_to_the_tour_machine(self):
        params = _spec()["driver"]["missionParams"]
        st = b47_jool_moon_tour.build_state(params)
        self.assertIsInstance(st, mlib.MtourState)
        st2, acts = b47_jool_moon_tour.decide(st, snap(ut=1.0))
        self.assertIsInstance(st2, mlib.MtourState)
        rows = b47_jool_moon_tour.evaluate([], params, st2)
        self.assertEqual("tourCompleted", rows[-1].name)
        self.assertEqual("b47_jool_moon_tour", b47_jool_moon_tour.SPEC.name)
        self.assertEqual(0, b47_jool_moon_tour.SPEC.settle_frames)

    def test_the_schema_declares_legs_only(self):
        with open(os.path.join(_MISSIONS, "b47_jool_moon_tour.schema.toml"), "rb") as fh:
            schema = tomllib.load(fh)
        self.assertEqual({"legs": {"required": True, "type": "list"}}, schema["params"])

    def test_the_flight_efficiency_map_names_the_machine(self):
        import flighteff
        self.assertEqual("mtour_decide", flighteff.MISSION_MACHINES["b47_jool_moon_tour"])

    def test_the_spec_validates_against_its_schema(self):
        import hlib
        schemas = {}
        for name in os.listdir(_MISSIONS):
            if name.endswith(".schema.toml"):
                with open(os.path.join(_MISSIONS, name), "rb") as fh:
                    schemas[name[:-len(".schema.toml")]] = tomllib.load(fh)
        with open(os.path.join(_HARNESS, "coverage", "registry.toml"), "rb") as fh:
            registry = tomllib.load(fh)
        v = hlib.validate_spec(_spec(), registry, mission_schemas=schemas)
        self.assertEqual([], list(v.errors))


if __name__ == "__main__":
    unittest.main()
