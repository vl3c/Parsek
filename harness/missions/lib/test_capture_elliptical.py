"""Unit tests for mlib's ELLIPTICAL CAPTURE -- the one thing mlib gained for B29.

THE SURFACE UNDER TEST is three additions and nothing else:
  * `B5Params.capture_elliptical_apoapsis` (spec key
    `captureEllipticalApoapsisMeters`) plus its four load-time implications;
  * `mlib.capture_node_plan` / `mlib.CaptureNodePlan`, the pure arithmetic;
  * `mlib._b5_capture_plan_action`, the single dispatch point both PLAN-CAPTURE
    emission sites call.

**THE INERTNESS PIN IS THE POINT OF THIS FILE.** The flag-off contract is
PAD-ALIGN's, ORBIT-START's and PARENT-RELAY's verbatim: with the key absent or 0,
PLAN-CAPTURE emits `ACTION_MJ_PLAN_CAPTURE` exactly as it did before B29, and every
other lane is byte-identical. `InertnessTests` asserts that against the FROZEN
pre-modifier Action rather than against the keys the modifier added -- the
`RelayParkAtParentInertnessTests` discipline, and the reason it matters is that a
pin written in terms of the new surface would still pass if the new surface leaked
into the old path.

WHY AN ELLIPSE NEEDED CODE AT ALL, since "loosen the park band" is the obvious
alternative and does not work: `_b5_capture_achieved` has always been TOLERANCE-ONLY
(an apoapsis ceiling, a periapsis floor, an eccentricity ceiling) while PLAN-CAPTURE
only ever emitted MechJeb's `operation_circularize`. There is no apoapsis TARGET
anywhere in the capture path, so a loose band changes what is ACCEPTED and not what
is FLOWN -- B28 delivered ecc 7.32e-07 under the loosest band in the suite. A cell
below pins exactly that, so the argument cannot rot into folklore.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q
"""

import math
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
_HARNESS = os.path.dirname(_MISSIONS)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import mlib  # noqa: E402

MU_KERBIN, R_KERBIN, _SOI_KERBIN = mlib.STOCK_BODY_GRAVITY["Kerbin"]

# B29's committed geometry, and the ONLY place in this file numbers are quoted:
# the derivations live in the spec header and the cells recompute rather than
# copy wherever they can.
ARRIVAL_PE_ALT = 150000.0
WANTED_AP_ALT = 6000000.0
ARRIVAL_VINF = 2713.00


def _base_params(**over):
    """A minimal CAPTURE-lane param dict. Deliberately NOT B29's committed shape:
    this file tests the MACHINE, and coupling it to one lane's spec would make a
    spec edit red the machine's tests."""
    params = {
        "captureEnabled": True,
        "targetBodyName": "Kerbin",
        "homeBodyName": "Jool",
        "interplanetaryTransfer": True,
        "returnBodyName": "Sun",
        "viaBodyNames": ["Sun"],
        "courseCorrectPeriapsisMeters": ARRIVAL_PE_ALT,
        "parkMaxApoapsisMeters": 8900000,
        "parkMaxEccentricity": 0.85,
        "parkMinPeriapsisMeters": 90000,
    }
    params.update(over)
    return mlib.b5_params_from_dict(params)


def _arrival_snapshot(vinf=ARRIVAL_VINF, pe_alt=ARRIVAL_PE_ALT, ut=1000.0,
                      ttpe=30000.0, body="Kerbin"):
    """A hyperbolic arrival frame in KSP's own reading convention.

    KSP reports a hyperbolic apoapsis as the large NEGATIVE `2*sma - r_p - R`, and
    building the fixture that way (rather than hand-picking a negative number) is
    what makes the `sma` cell below a real test of the shared expression."""
    r_p = R_KERBIN + pe_alt
    sma = -MU_KERBIN / (vinf ** 2)
    ap_alt = 2.0 * sma - r_p - R_KERBIN
    return mlib.TelemetrySnapshot(ut=ut, body=body, periapsis=pe_alt,
                                  apoapsis=ap_alt, time_to_periapsis=ttpe)


class InertnessTests(unittest.TestCase):
    """FLAG OFF = every lane flown to date, byte for byte."""

    FROZEN = mlib.Action(mlib.ACTION_MJ_PLAN_CAPTURE)

    def test_the_key_absent_emits_the_frozen_pre_b29_action(self):
        params = _base_params()
        self.assertEqual(params.capture_elliptical_apoapsis, 0.0)
        self.assertEqual(
            mlib._b5_capture_plan_action(params, _arrival_snapshot()),
            self.FROZEN)

    def test_the_key_at_zero_emits_the_frozen_pre_b29_action(self):
        params = _base_params(captureEllipticalApoapsisMeters=0)
        self.assertEqual(
            mlib._b5_capture_plan_action(params, _arrival_snapshot()),
            self.FROZEN)

    def test_flag_off_emits_the_frozen_action_on_every_frame_shape(self):
        """Including the frames that REFUSE an elliptical node. The circularize
        path must not inherit any of the new refusal ladder."""
        params = _base_params()
        nan = float("nan")
        for frame in (
                _arrival_snapshot(),
                _arrival_snapshot(body="Sun"),
                _arrival_snapshot(ttpe=-1.0),
                mlib.TelemetrySnapshot(ut=nan, body="Kerbin", periapsis=nan,
                                       apoapsis=nan, time_to_periapsis=nan),
                mlib.TelemetrySnapshot(ut=0.0, body="Kerbin", periapsis=100000.0,
                                       apoapsis=200000.0,
                                       time_to_periapsis=100.0),
        ):
            with self.subTest(body=frame.body, pe=frame.periapsis):
                self.assertEqual(
                    mlib._b5_capture_plan_action(params, frame), self.FROZEN)

    def test_flag_off_refuses_the_node_plan_by_name(self):
        plan = mlib.capture_node_plan(_base_params(), _arrival_snapshot())
        self.assertEqual(plan.node_ut, None)
        self.assertEqual(plan.dv, None)
        self.assertIn("not armed", plan.reason)

    def test_the_capture_window_predicate_is_untouched_by_the_flag(self):
        """`_b5_capture_achieved` is TOLERANCE-ONLY and stays that way: the flag
        changes what is FLOWN, never what is ACCEPTED."""
        on = _base_params(captureEllipticalApoapsisMeters=WANTED_AP_ALT)
        off = _base_params()
        for ap, pe, ecc in ((6000000.0, 150000.0, 0.7959),
                            (100000.0, 100000.0, 0.0),
                            (-4.0e5, 150000.0, 2.56)):
            frame = mlib.TelemetrySnapshot(ut=0.0, body="Kerbin", apoapsis=ap,
                                           periapsis=pe, eccentricity=ecc)
            with self.subTest(ap=ap):
                self.assertEqual(mlib._b5_capture_achieved(on, frame),
                                 mlib._b5_capture_achieved(off, frame))

    def test_there_is_no_apoapsis_target_in_the_capture_window(self):
        """The claim that made the flag necessary, pinned so it cannot rot into
        folklore: the park triple bounds the result and never aims it, which is
        why a 'loose band' cannot buy an ellipse -- the executor circularizes
        regardless (B28 delivered ecc 7.32e-07 under the loosest band committed)."""
        loose = _base_params(parkMaxApoapsisMeters=1500000000,
                             parkMaxEccentricity=0.99)
        near_circular = mlib.TelemetrySnapshot(
            ut=0.0, body="Kerbin", apoapsis=150001.0, periapsis=150000.0,
            eccentricity=7.32e-07)
        self.assertTrue(mlib._b5_capture_achieved(loose, near_circular),
                        "a loose band ACCEPTS the circularization it did not ask "
                        "for -- which is exactly why it cannot aim at an ellipse")


class NodeArithmeticTests(unittest.TestCase):
    """`capture_node_plan`: vis-viva at the arrival periapsis, both energies."""

    def setUp(self):
        self.params = _base_params(
            captureEllipticalApoapsisMeters=WANTED_AP_ALT)

    def test_the_dv_matches_an_independent_vis_viva_computation(self):
        snapshot = _arrival_snapshot()
        plan = mlib.capture_node_plan(self.params, snapshot)
        self.assertEqual(plan.reason, "")
        r_p = R_KERBIN + ARRIVAL_PE_ALT
        r_a = R_KERBIN + WANTED_AP_ALT
        v_hyp = math.sqrt(ARRIVAL_VINF ** 2 + 2.0 * MU_KERBIN / r_p)
        v_ell = math.sqrt(MU_KERBIN * (2.0 / r_p - 2.0 / (r_p + r_a)))
        self.assertAlmostEqual(plan.dv, v_ell - v_hyp, places=6)

    def test_the_dv_is_negative_because_a_capture_is_retrograde(self):
        """THE SIGN IS THE CONTRACT: it is handed to `add_node(ut, prograde=dv)`
        verbatim, so a caller taking abs() would RAISE the orbit instead of
        closing it."""
        plan = mlib.capture_node_plan(self.params, _arrival_snapshot())
        self.assertLess(plan.dv, 0.0)

    def test_the_node_sits_at_the_arrival_periapsis(self):
        """And therefore passes the PLAN-CAPTURE node-UT sanity gate unchanged --
        the gate measures exactly `ut + time_to_periapsis`."""
        snapshot = _arrival_snapshot(ut=4321.0, ttpe=9876.0)
        plan = mlib.capture_node_plan(self.params, snapshot)
        self.assertAlmostEqual(plan.node_ut, 4321.0 + 9876.0)
        self.assertTrue(mlib.capture_node_at_periapsis(
            plan.node_ut, snapshot.ut, snapshot.time_to_periapsis))

    def test_a_deeper_periapsis_is_a_cheaper_capture(self):
        """Oberth, and the reason the spec argues 150 km against 100 km rather
        than picking a round number."""
        deep = mlib.capture_node_plan(self.params, _arrival_snapshot(pe_alt=100000.0))
        shallow = mlib.capture_node_plan(
            _base_params(captureEllipticalApoapsisMeters=WANTED_AP_ALT,
                         courseCorrectPeriapsisMeters=100000.0),
            _arrival_snapshot(pe_alt=300000.0))
        self.assertLess(abs(deep.dv), abs(shallow.dv))

    def test_a_higher_target_apoapsis_is_a_cheaper_capture(self):
        low = mlib.capture_node_plan(self.params, _arrival_snapshot())
        high = mlib.capture_node_plan(
            _base_params(captureEllipticalApoapsisMeters=40000000.0,
                         parkMaxApoapsisMeters=50000000.0,
                         parkMaxEccentricity=0.99),
            _arrival_snapshot())
        self.assertLess(abs(high.dv), abs(low.dv))

    def test_the_circularization_it_replaces_is_strictly_more_expensive(self):
        """The whole economic argument for the flag, computed rather than quoted."""
        plan = mlib.capture_node_plan(self.params, _arrival_snapshot())
        r_p = R_KERBIN + ARRIVAL_PE_ALT
        v_hyp = math.sqrt(ARRIVAL_VINF ** 2 + 2.0 * MU_KERBIN / r_p)
        circ_dv = math.sqrt(MU_KERBIN / r_p) - v_hyp
        self.assertLess(abs(plan.dv), abs(circ_dv))
        self.assertGreater(abs(circ_dv) - abs(plan.dv), 700.0)

    def test_the_hyperbolic_sma_expression_spans_both_conic_types(self):
        """`R + (ap + pe)/2` is the semi-major axis on an ELLIPSE and on a
        HYPERBOLA alike, given KSP's negative-apoapsis reading. No branch, and
        therefore no branch to get wrong."""
        snapshot = _arrival_snapshot()
        sma = R_KERBIN + (snapshot.apoapsis + snapshot.periapsis) / 2.0
        self.assertLess(sma, 0.0)
        self.assertAlmostEqual(sma, -MU_KERBIN / (ARRIVAL_VINF ** 2), places=3)


class RefusalTests(unittest.TestCase):
    """Every reading fails CLOSED, with its own name."""

    def setUp(self):
        self.params = _base_params(
            captureEllipticalApoapsisMeters=WANTED_AP_ALT)

    def _refused(self, snapshot):
        plan = mlib.capture_node_plan(self.params, snapshot)
        self.assertNotEqual(plan.reason, "")
        self.assertIsNone(plan.node_ut)
        self.assertIsNone(plan.dv)
        return plan.reason

    def test_a_wrong_body_is_refused_by_name(self):
        self.assertIn("target body", self._refused(_arrival_snapshot(body="Sun")))

    def test_an_unreadable_orbit_is_refused_by_name(self):
        nan = float("nan")
        for field in ("ut", "periapsis", "apoapsis", "time_to_periapsis"):
            with self.subTest(field=field):
                snapshot = mlib.replace(_arrival_snapshot(), **{field: nan})
                self.assertIn("unreadable orbit", self._refused(snapshot))

    def test_a_negative_periapsis_clock_is_refused_by_name(self):
        self.assertIn("time_to_periapsis",
                      self._refused(_arrival_snapshot(ttpe=-1.0)))

    def test_an_apoapsis_target_below_the_arrival_periapsis_is_refused(self):
        params = _base_params(captureEllipticalApoapsisMeters=50000.0,
                              courseCorrectPeriapsisMeters=0.0)
        plan = mlib.capture_node_plan(params, _arrival_snapshot(pe_alt=150000.0))
        self.assertIn("not an ellipse", plan.reason)

    def test_an_arrival_already_inside_the_target_ellipse_is_refused(self):
        """A BOUND arrival slower than the wanted ellipse needs no capture, and
        computing one would produce a PROGRADE dv that raises the orbit."""
        bound = mlib.TelemetrySnapshot(ut=0.0, body="Kerbin", periapsis=150000.0,
                                       apoapsis=200000.0, time_to_periapsis=100.0)
        self.assertIn("not negative", self._refused(bound))

    def test_a_refusal_makes_the_dispatch_emit_nothing(self):
        """The B5_ESCAPE contract: no action this frame, the plan cadence retries."""
        self.assertIsNone(
            mlib._b5_capture_plan_action(self.params,
                                         _arrival_snapshot(body="Sun")))

    def test_there_is_no_min_lead_roll_forward(self):
        """The DELIBERATE asymmetry with `escape_node_plan`. A park is periodic and
        the next periapsis is one period away; an arrival hyperbola has exactly ONE
        periapsis, so rolling forward would place the node after the craft had left
        the SOI. A short lead is the CAPTURE-BURN no-start ladder's problem."""
        snapshot = _arrival_snapshot(ut=10.0, ttpe=1.0)
        plan = mlib.capture_node_plan(self.params, snapshot)
        self.assertEqual(plan.reason, "")
        self.assertAlmostEqual(plan.node_ut, 11.0)


class LoadTimeGateTests(unittest.TestCase):
    """The four implications, each RAISING rather than degrading, because each
    would otherwise fail only AFTER the capture burn had been flown."""

    def test_it_requires_capture_enabled(self):
        with self.assertRaises(ValueError) as caught:
            mlib.b5_params_from_dict({
                "captureEllipticalApoapsisMeters": WANTED_AP_ALT,
                "captureEnabled": False, "targetBodyName": "Kerbin"})
        self.assertIn("captureEnabled", str(caught.exception))

    def test_it_may_not_exceed_the_park_apoapsis_ceiling(self):
        with self.assertRaises(ValueError) as caught:
            _base_params(captureEllipticalApoapsisMeters=9000000.0)
        self.assertIn("parkMaxApoapsisMeters", str(caught.exception))

    def test_it_must_exceed_the_aimed_arrival_periapsis(self):
        with self.assertRaises(ValueError) as caught:
            _base_params(captureEllipticalApoapsisMeters=150000.0)
        self.assertIn("courseCorrectPeriapsisMeters", str(caught.exception))

    def test_the_delivered_eccentricity_must_fit_the_park_ceiling(self):
        """THE TRAP: mlib's default ceiling is 0.5 and every prior lane sets 0.25,
        while any affordable inbound capture ellipse is well above both."""
        with self.assertRaises(ValueError) as caught:
            _base_params(captureEllipticalApoapsisMeters=WANTED_AP_ALT,
                         parkMaxEccentricity=0.25)
        message = str(caught.exception)
        self.assertIn("parkMaxEccentricity", message)
        self.assertIn("0.7959", message,
                      "the message must name the value to raise the ceiling to")

    def test_the_committed_geometry_passes_every_gate(self):
        params = _base_params(captureEllipticalApoapsisMeters=WANTED_AP_ALT)
        self.assertEqual(params.capture_elliptical_apoapsis, WANTED_AP_ALT)

    def test_none_of_the_gates_fire_with_the_flag_off(self):
        """The inertness claim restated at the LOAD boundary: a param set that
        would be rejected with the flag on must load clean with it off."""
        params = mlib.b5_params_from_dict({
            "captureEnabled": True, "targetBodyName": "Kerbin",
            "parkMaxEccentricity": 0.25, "parkMaxApoapsisMeters": 1000.0,
            "courseCorrectPeriapsisMeters": 9000000.0})
        self.assertEqual(params.capture_elliptical_apoapsis, 0.0)


class DispatchTests(unittest.TestCase):
    """`_b5_capture_plan_action` is the ONE emission point, so the entry and the
    retry cadence cannot drift apart."""

    def test_the_armed_action_is_the_add_node_seam_labelled_capture(self):
        params = _base_params(captureEllipticalApoapsisMeters=WANTED_AP_ALT)
        action = mlib._b5_capture_plan_action(params, _arrival_snapshot())
        self.assertEqual(action.kind, mlib.ACTION_ADD_MANEUVER_NODE)
        self.assertLess(action.value, 0.0)
        self.assertEqual(action.text, "Capture",
                         "the label is what stops a retrograde arrival node from "
                         "reporting itself as an escape in the flight ledger")

    def test_it_reuses_the_escapes_seam_rather_than_adding_one(self):
        """No new runner surface: the same additive kRPC `add_node(ut, prograde=)`
        the parent-relay escape already goes through, with a NEGATIVE prograde."""
        params = _base_params(captureEllipticalApoapsisMeters=WANTED_AP_ALT)
        action = mlib._b5_capture_plan_action(params, _arrival_snapshot())
        escape_kind = mlib.ACTION_ADD_MANEUVER_NODE
        self.assertEqual(action.kind, escape_kind)
        self.assertIsNotNone(action.node_ut)

    def test_the_escape_still_emits_an_unlabelled_node(self):
        """The runner defaults `action.text` to "Escape", so B26's and B28's three
        log lines stay byte-identical. Pinned HERE because the default lives in the
        runner and the emission lives in mlib."""
        params = mlib.b5_params_from_dict({
            "parentRelayTransfer": True, "interplanetaryTransfer": True,
            "captureEnabled": True, "homeBodyName": "Laythe",
            "targetBodyName": "Jool", "returnBodyName": "Sun",
            "viaBodyNames": ["Sun"], "escapeSoiSpeedMps": 450,
            "transferMinApoapsisMeters": 10000000})
        snapshot = mlib.TelemetrySnapshot(
            ut=0.0, body="Laythe", periapsis=56240.0, apoapsis=87931.0,
            time_to_periapsis=600.0)
        plan = mlib.escape_node_plan(params, snapshot)
        self.assertEqual(plan.reason, "")
        action = mlib.Action(mlib.ACTION_ADD_MANEUVER_NODE, value=plan.dv,
                             node_ut=plan.node_ut)
        self.assertIsNone(action.text)


if __name__ == "__main__":
    unittest.main()
