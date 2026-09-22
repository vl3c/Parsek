"""Tests for mission rf12s_refly_orbit_insert (scenario RF-12S): the pure
``mlib.rfo_decide`` burn machine, its assertion evaluator, the schema/params sync and
the thin shell. Stdlib unittest only; no kRPC."""

from __future__ import annotations

import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
for _p in (_HERE, _MISSIONS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import mlib  # noqa: E402
import rf12s_refly_orbit_insert as rfo_shell  # noqa: E402

SCHEMA_PATH = os.path.join(_MISSIONS, "rf12s_refly_orbit_insert.schema.toml")
SHELL_PATH = os.path.join(_MISSIONS, "rf12s_refly_orbit_insert.py")


def snap(**kw):
    base = dict(ut=140.0, altitude=40000.0, apoapsis=94000.0, periapsis=-560000.0,
                situation="FLYING", crew_count=2, available_thrust=0.0,
                liquid_fuel=720.0)
    base.update(kw)
    return mlib.TelemetrySnapshot(**base)


def fresh(**params):
    return mlib.rfo_initial_state(mlib.rfo_params_from_dict(params))


def burning(**params):
    st, _ = mlib.rfo_decide(fresh(**params), snap())
    assert st.phase == mlib.RFO_BURN
    return st


class RfoParamsTests(unittest.TestCase):
    def test_defaults_are_the_sweep_choice(self):
        p = mlib.rfo_params_from_dict({})
        self.assertEqual(-10.0, p.pitch_deg)
        self.assertEqual(90.0, p.heading_deg)
        self.assertEqual(75000.0, p.target_periapsis)
        self.assertEqual(2, p.periapsis_debounce)

    def test_every_key_is_read(self):
        p = mlib.rfo_params_from_dict({
            "pitchDeg": -5, "headingDeg": 88, "throttle": 0.8,
            "targetPeriapsisMeters": 80000, "periapsisDebounceFrames": 3,
            "minStartAltitudeMeters": 20000, "minCrew": 2, "igniteFrames": 10,
            "burnFrames": 300})
        self.assertEqual((-5.0, 88.0, 0.8, 80000.0, 3, 20000.0, 2, 10, 300),
                         (p.pitch_deg, p.heading_deg, p.throttle, p.target_periapsis,
                          p.periapsis_debounce, p.min_start_altitude, p.min_crew,
                          p.ignite_frames, p.burn_frames))


class RfoHandoffTests(unittest.TestCase):
    def test_airborne_crewed_handoff_ignites_with_three_actions(self):
        st, actions = mlib.rfo_decide(fresh(), snap())
        self.assertEqual(mlib.RFO_BURN, st.phase)
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE, mlib.ACTION_SET_THROTTLE,
                          mlib.ACTION_AP_SET_PITCH_HEADING], [a.kind for a in actions])
        self.assertEqual(1.0, actions[1].value)
        self.assertEqual((-10.0, 90.0), actions[2].pitch_heading)
        self.assertEqual(("FLYING", 40000.0, 2),
                         (st.start_situation, st.start_altitude, st.start_crew))

    def test_not_airborne_refuses_by_name_and_emits_nothing(self):
        st, actions = mlib.rfo_decide(fresh(), snap(situation="PRE_LAUNCH"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("handoff not airborne", st.loss_reason)
        self.assertEqual([], actions)

    def test_low_altitude_refuses(self):
        st, _ = mlib.rfo_decide(fresh(), snap(altitude=500.0))
        self.assertIn("handoff below", st.loss_reason)

    def test_crewless_handoff_refuses(self):
        st, _ = mlib.rfo_decide(fresh(minCrew=2), snap(crew_count=1))
        self.assertIn("handoff crew 1 < minCrew 2", st.loss_reason)

    def test_unread_crew_sentinel_fails_closed(self):
        st, _ = mlib.rfo_decide(fresh(), snap(crew_count=-1))
        self.assertIn("handoff crew -1", st.loss_reason)

    def test_sub_orbital_handoff_is_airborne(self):
        st, _ = mlib.rfo_decide(fresh(), snap(situation="SUB_ORBITAL"))
        self.assertEqual(mlib.RFO_BURN, st.phase)


class RfoBurnTests(unittest.TestCase):
    def test_debounced_periapsis_cuts_and_reaches_orbit(self):
        st = burning()
        st, a = mlib.rfo_decide(st, snap(available_thrust=250000.0, periapsis=-100000.0))
        self.assertEqual([], a)
        st, a = mlib.rfo_decide(st, snap(available_thrust=250000.0, periapsis=76000.0))
        self.assertEqual([], a)  # one frame is not enough
        st, a = mlib.rfo_decide(st, snap(ut=240.0, available_thrust=250000.0,
                                         periapsis=77000.0, apoapsis=700000.0,
                                         liquid_fuel=200.0))
        self.assertEqual(mlib.RFO_ORBIT, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_AP_DISENGAGE],
                         [x.kind for x in a])
        self.assertEqual((77000.0, 700000.0, 240.0, 200.0),
                         (st.cut_periapsis, st.cut_apoapsis, st.cut_ut, st.cut_liquid_fuel))

    def test_a_dip_resets_the_streak(self):
        st = burning()
        st, _ = mlib.rfo_decide(st, snap(available_thrust=1.0, periapsis=76000.0))
        st, _ = mlib.rfo_decide(st, snap(available_thrust=1.0, periapsis=74000.0))
        self.assertEqual(0, st.pe_streak)
        st, _ = mlib.rfo_decide(st, snap(available_thrust=1.0, periapsis=76000.0))
        self.assertEqual(mlib.RFO_BURN, st.phase)

    def test_periapsis_without_ignition_never_cuts(self):
        # A periapsis read above target on an unlit engine is not an insertion.
        st = burning()
        for _ in range(3):
            st, _ = mlib.rfo_decide(st, snap(available_thrust=0.0, periapsis=90000.0))
        self.assertEqual(mlib.RFO_BURN, st.phase)

    def test_engine_never_lit_fails_by_name(self):
        st = burning(igniteFrames=3)
        for _ in range(3):
            st, a = mlib.rfo_decide(st, snap(available_thrust=0.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("engine never lit", st.loss_reason)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], [x.kind for x in a])

    def test_flameout_short_of_orbit_fails_by_name(self):
        st = burning()
        st, _ = mlib.rfo_decide(st, snap(available_thrust=250000.0))
        st, a = mlib.rfo_decide(st, snap(available_thrust=0.0, periapsis=20000.0))
        self.assertIn("propellant exhausted short of orbit", st.loss_reason)
        self.assertIn(mlib.ACTION_CUT_THROTTLE, [x.kind for x in a])

    def test_unreadable_thrust_is_not_a_flameout(self):
        st = burning()
        st, _ = mlib.rfo_decide(st, snap(available_thrust=250000.0))
        st, _ = mlib.rfo_decide(st, snap(available_thrust=float("nan")))
        self.assertFalse(st.done)

    def test_burn_bound_flakes_by_name(self):
        st = burning(burnFrames=2)
        st, _ = mlib.rfo_decide(st, snap(available_thrust=1.0))
        st, _ = mlib.rfo_decide(st, snap(available_thrust=1.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("burn exceeded 2 frames", st.flake_reason)

    def test_vessel_lost_is_assert_fail(self):
        st, _ = mlib.rfo_decide(burning(), snap(vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("vessel-lost in phase BURN", st.loss_reason)

    def test_done_state_is_inert(self):
        st, _ = mlib.rfo_decide(fresh(), snap(situation="LANDED"))
        again, actions = mlib.rfo_decide(st, snap())
        self.assertIs(st, again)
        self.assertEqual([], actions)


class RfoAssertionTests(unittest.TestCase):
    def _orbit_state(self):
        st = burning()
        st, _ = mlib.rfo_decide(st, snap(available_thrust=1.0, periapsis=76000.0))
        st, _ = mlib.rfo_decide(st, snap(available_thrust=1.0, periapsis=76000.0))
        return st

    def test_all_rows_met_on_orbit(self):
        st = self._orbit_state()
        rows = mlib.evaluate_rfo_assertions([], st.params, st)
        self.assertEqual(["handoffAirborneCrewed", "engineLit", "orbitAboveAtmosphere"],
                         [r.name for r in rows])
        self.assertTrue(all(r.met for r in rows))
        verdict, _ = mlib.resolve_flight_verdict(st, rows)
        self.assertEqual(mlib.MISSION_OK, verdict)

    def test_refused_handoff_is_not_mission_ok(self):
        st, _ = mlib.rfo_decide(fresh(), snap(crew_count=0))
        rows = mlib.evaluate_rfo_assertions([], st.params, st)
        self.assertFalse(rows[0].met)
        verdict, reason = mlib.resolve_flight_verdict(st, rows)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, verdict)
        self.assertIn("handoff crew", reason)

    def test_rows_serialize(self):
        st = self._orbit_state()
        for row in mlib.evaluate_rfo_assertions([], st.params, st):
            self.assertIn("name", row.to_dict())


class RfoSchemaAndShellTests(unittest.TestCase):
    def test_schema_declares_exactly_the_keys_the_params_reader_reads(self):
        with open(SCHEMA_PATH, "rb") as f:
            schema = tomllib.load(f)
        declared = set(schema["params"].keys())
        self.assertEqual({"pitchDeg", "headingDeg", "throttle", "targetPeriapsisMeters",
                          "periapsisDebounceFrames", "minStartAltitudeMeters", "minCrew",
                          "igniteFrames", "burnFrames"}, declared)
        with open(os.path.join(_HERE, "mlib.py"), encoding="utf-8") as f:
            src = f.read()
        body = src[src.index("def rfo_params_from_dict"):src.index("class RfoState")]
        for key in declared:
            self.assertIn('"%s"' % key, body)

    def test_shell_spec_shape(self):
        self.assertEqual("rf12s_refly_orbit_insert", rfo_shell.SPEC.name)
        self.assertFalse(rfo_shell.SPEC.allow_rails_warp)
        self.assertEqual(0.0, rfo_shell.SPEC.max_physics_warp)
        self.assertEqual(0, rfo_shell.SPEC.settle_frames)
        st = rfo_shell.build_state({"pitchDeg": -7})
        self.assertEqual(-7.0, st.params.pitch_deg)

    def test_shell_has_no_module_top_krpc_import(self):
        with open(SHELL_PATH, encoding="utf-8") as f:
            for line in f:
                self.assertFalse(line.startswith("import krpc")
                                 or line.startswith("from krpc"))


if __name__ == "__main__":
    unittest.main()
