"""Unit tests for the b23_ike_orbit lane: the ORBIT-START entry door on the
shared B5 machine (the entry gate, the phase route, the two named give-ups, the
startedInHomeOrbit assertion row) and -- the load-bearing half -- the proof that
the flag is INERT when off.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

Every cell names the regression it guards. The lane's whole premise is that a
recording can be ROOTED at a body other than Kerbin, which requires the machine
to start from an already-parked fixture; the cells here pin the door that makes
that possible AND the fact that opening it moved nothing on the twenty-plus
flown lanes that do not use it.
"""

import os
import tomllib
import unittest
from dataclasses import replace

import mlib
from mlib import TelemetrySnapshot

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))


def snap(**kw):
    return TelemetrySnapshot(**kw)


def b23_params(**overrides):
    """A minimal B23-shaped missionParams dict (orbit-start + moon Hohmann +
    capture), overridable per cell. Deliberately NOT the committed spec: a cell
    that needs the committed numbers reads the .toml (see SpecArithmeticTests)."""
    base = {
        "startInOrbit": True,
        "startInOrbitMinPeriapsisMeters": 300000,
        "startInOrbitMaxApoapsisMeters": 1500000,
        "startInOrbitMaxEccentricity": 0.05,
        "startInOrbitSituations": ["ORBITING"],
        "startInOrbitDebounceFrames": 3,
        "startInOrbitSettleSeconds": 120,
        "homeBodyName": "Duna",
        "targetBodyName": "Ike",
        "interplanetaryTransfer": False,
        "returnBodyName": "Duna",
        "transferMinApoapsisMeters": 2000000,
        "courseCorrectPeriapsisMeters": 150000,
        "maxCorrectionDvMps": 80,
        "correctionTriggerAltsMeters": [0, 1500000],
        "planTimeoutSeconds": 300,
        "transferBurnTimeoutSeconds": 40000,
        "coastTimeoutSeconds": 120000,
        "flybyTimeoutSeconds": 60000,
        "targetPeriapsisFloorMeters": 20000,
        "captureEnabled": True,
        "capturePlanTimeoutSeconds": 300,
        "captureBurnTimeoutSeconds": 60000,
        "parkMinPeriapsisMeters": 20000,
        "parkMaxApoapsisMeters": 500000,
        "parkMaxEccentricity": 0.25,
        "parkDwellSeconds": 180,
        "parkTimeoutSeconds": 600,
    }
    base.update(overrides)
    return base


def parked_snap(**overrides):
    """A frame reading the committed fixture's MEASURED Duna park (SMA
    1,038,214.95 / ECC 0.0012696 / alt 718,363 m at save UT 9,160,396.76)."""
    base = dict(ut=9_160_396.76, body="Duna", situation="ORBITING",
                apoapsis=719_680.0, periapsis=717_047.0,
                eccentricity=0.0012696, altitude=718_363.0)
    base.update(overrides)
    return snap(**base)


class OrbitStartParamsTests(unittest.TestCase):
    def test_the_seven_keys_parse_and_default_off(self):
        on = mlib.b5_params_from_dict(b23_params())
        self.assertTrue(on.start_in_orbit)
        self.assertEqual(on.start_in_orbit_min_periapsis, 300000.0)
        self.assertEqual(on.start_in_orbit_max_apoapsis, 1500000.0)
        self.assertEqual(on.start_in_orbit_max_eccentricity, 0.05)
        self.assertEqual(on.start_in_orbit_situations, ("ORBITING",))
        self.assertEqual(on.start_in_orbit_debounce, 3)
        self.assertEqual(on.start_in_orbit_settle_seconds, 120.0)

    def test_absent_keys_default_to_the_inert_shape(self):
        # A lane that says NOTHING about orbit-start must read as flag-off with
        # gate values that could never accidentally admit anything: the flag off
        # is what makes every conjunct unreachable.
        off = mlib.b5_params_from_dict({})
        self.assertFalse(off.start_in_orbit)
        self.assertEqual(off.start_in_orbit_min_periapsis, 0.0)
        self.assertEqual(off.start_in_orbit_max_apoapsis, 0.0)
        self.assertEqual(off.start_in_orbit_max_eccentricity, 1.0)
        self.assertEqual(off.start_in_orbit_debounce, 3)

    def test_start_in_orbit_and_pad_align_are_mutually_exclusive(self):
        # PAD-ALIGN jumps the epoch ON THE PAD and then flies an ascent; there is
        # no pad here. A spec asking for both is asking the machine to align a
        # pad it will not use, and the two PRELAUNCH branches would silently
        # race on branch order rather than on intent.
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(b23_params(
                padAlignEjection=True, interplanetaryTransfer=True,
                homeBodyName="Kerbin", targetBodyName="Duna"))


class OrbitStartGateTests(unittest.TestCase):
    """The pure per-frame classifier. Three-valued on purpose: UNREADABLE and
    OUT-OF-GATE earn DIFFERENT give-ups (transient vs deterministic), and
    collapsing them would price a fixture fault as a flake and spend a retry on
    a run that can only fail again."""

    def setUp(self):
        self.params = mlib.b5_params_from_dict(b23_params())

    def _verdict(self, **overrides):
        return mlib.start_in_orbit_frame_verdict(
            self.params, parked_snap(**overrides))

    def test_the_committed_fixtures_park_is_in_gate(self):
        # THE cell that ties the gate to the fixture it was sized against: if a
        # future edit tightens a conjunct past the real park, this reds instead
        # of the flight doing so.
        verdict, reason = self._verdict()
        self.assertEqual(verdict, mlib.START_IN_ORBIT_IN_GATE, reason)
        self.assertEqual(reason, "")

    def test_flag_off_is_the_off_verdict_and_judges_nothing(self):
        off = mlib.b5_params_from_dict(b23_params(startInOrbit=False))
        # A frame that would FAIL every conjunct still reads OFF, never a
        # failure: the classifier must not be able to fail a lane that never
        # asked for it.
        verdict, reason = mlib.start_in_orbit_frame_verdict(
            off, snap(body="Kerbin", situation="PRE_LAUNCH", apoapsis=-1.0))
        self.assertEqual(verdict, mlib.START_IN_ORBIT_OFF)
        self.assertEqual(reason, "")

    def test_a_blank_body_is_unreadable_not_a_failure(self):
        verdict, reason = self._verdict(body="")
        self.assertEqual(verdict, mlib.START_IN_ORBIT_UNREADABLE)
        self.assertIn("body", reason)

    def test_a_nan_orbit_field_is_unreadable_fail_closed(self):
        for field in ("apoapsis", "periapsis", "eccentricity"):
            with self.subTest(field=field):
                verdict, _ = self._verdict(**{field: float("nan")})
                self.assertEqual(verdict, mlib.START_IN_ORBIT_UNREADABLE)

    def test_a_blank_situation_is_unreadable_not_a_pass(self):
        verdict, reason = self._verdict(situation="")
        self.assertEqual(verdict, mlib.START_IN_ORBIT_UNREADABLE)
        self.assertIn("situation", reason)

    def test_the_wrong_soi_body_is_out_of_gate(self):
        verdict, reason = self._verdict(body="Kerbin")
        self.assertEqual(verdict, mlib.START_IN_ORBIT_OUT_OF_GATE)
        self.assertIn("Kerbin", reason)
        self.assertIn("Duna", reason)

    def test_a_suborbital_situation_is_out_of_gate(self):
        verdict, reason = self._verdict(situation="SUB_ORBITAL")
        self.assertEqual(verdict, mlib.START_IN_ORBIT_OUT_OF_GATE)
        self.assertIn("SUB_ORBITAL", reason)

    def test_a_negative_apoapsis_is_out_of_gate_even_with_no_ceiling(self):
        # The bound-orbit evidence, and it must hold with the ceiling DISABLED:
        # a hyperbolic / escaping approach reads a NEGATIVE apoapsis, and
        # "ap <= ceiling" is trivially satisfied by a negative number.
        params = mlib.b5_params_from_dict(
            b23_params(startInOrbitMaxApoapsisMeters=0))
        verdict, reason = mlib.start_in_orbit_frame_verdict(
            params, parked_snap(apoapsis=-4_000_000.0))
        self.assertEqual(verdict, mlib.START_IN_ORBIT_OUT_OF_GATE)
        self.assertIn("BOUND", reason)

    def test_an_apoapsis_above_the_ceiling_is_out_of_gate(self):
        # A start park drifted up into Ike's own shell (~2,880 km altitude) is
        # an unplanned encounter waiting to happen.
        verdict, reason = self._verdict(apoapsis=2_900_000.0)
        self.assertEqual(verdict, mlib.START_IN_ORBIT_OUT_OF_GATE)
        self.assertIn("ceiling", reason)

    def test_a_periapsis_below_the_floor_is_out_of_gate(self):
        verdict, reason = self._verdict(periapsis=40_000.0)
        self.assertEqual(verdict, mlib.START_IN_ORBIT_OUT_OF_GATE)
        self.assertIn("floor", reason)

    def test_an_eccentric_park_is_out_of_gate(self):
        # THE conjunct with no second line of defence: this lane never flies
        # CIRCULARIZE, so parkTrimEccMax cannot round an eccentric start park
        # out, and the Hohmann planner's burn sizing is the B15 shortfall
        # waiting to happen.
        verdict, reason = self._verdict(eccentricity=0.4)
        self.assertEqual(verdict, mlib.START_IN_ORBIT_OUT_OF_GATE)
        self.assertIn("eccentricity", reason)


class OrbitStartMachineTests(unittest.TestCase):
    """The PRELAUNCH branch: debounce, the phase route, and the two named
    give-ups."""

    def setUp(self):
        self.params = mlib.b5_params_from_dict(b23_params())
        self.state = mlib.b5_initial_state(self.params)

    def _run(self, frames):
        state = self.state
        emitted = []
        for f in frames:
            state, actions = mlib.b5_decide(state, f)
            emitted.append(actions)
        return state, emitted

    def test_the_debounce_must_be_earned_in_full(self):
        state, emitted = self._run([parked_snap(ut=9_160_396.76 + i)
                                    for i in range(2)])
        self.assertEqual(state.phase, mlib.B5_PRELAUNCH)
        self.assertEqual(state.start_in_orbit_streak, 2)
        self.assertEqual([a for acts in emitted for a in acts], [])

    def test_three_in_gate_frames_enter_orbit_and_emit_nothing(self):
        # NO ACTIONS on entry is the contract: the fixture is already parked
        # with its throttle cut, and the ascent kickoff actions are precisely
        # what this mode must not fire.
        state, emitted = self._run([parked_snap(ut=9_160_396.76 + i)
                                    for i in range(3)])
        self.assertEqual(state.phase, mlib.B5_ORBIT)
        self.assertEqual([a for acts in emitted for a in acts], [])
        self.assertEqual(state.launch_ut, 9_160_398.76)
        self.assertEqual(state.start_in_orbit_entry_eccentricity, 0.0012696)

    def test_an_out_of_gate_frame_resets_the_streak(self):
        frames = [parked_snap(ut=9_160_396.76), parked_snap(ut=9_160_397.76),
                  parked_snap(ut=9_160_398.76, body="Kerbin"),
                  parked_snap(ut=9_160_399.76)]
        state, _ = self._run(frames)
        self.assertEqual(state.phase, mlib.B5_PRELAUNCH)
        self.assertEqual(state.start_in_orbit_streak, 1)

    def test_the_orbit_waypoint_hands_straight_to_plan_transfer(self):
        # The route the whole lane depends on: ORBIT is entered as a one-frame
        # waypoint and the NEXT frame is the SAME _b5_enter_plan_transfer
        # hand-off an ascent-flown park takes -- one code path, two
        # predecessors.
        state, _ = self._run([parked_snap(ut=9_160_396.76 + i) for i in range(3)])
        state, actions = mlib.b5_decide(state, parked_snap(ut=9_160_400.76))
        self.assertEqual(state.phase, mlib.B5_PLAN_TRANSFER)
        kinds = [a.kind for a in actions]
        self.assertEqual(kinds, [mlib.ACTION_SET_TARGET_BODY,
                                 mlib.ACTION_MJ_PLAN_TRANSFER])
        self.assertEqual(actions[0].text, "Ike")

    def test_reached_orbit_is_satisfied_by_the_orbit_start_entry(self):
        # Entering ORBIT rather than PLAN-TRANSFER directly is what keeps the
        # reachedOrbit assertion on ONE evidence source instead of growing a
        # mode-dependent second one.
        state, _ = self._run([parked_snap(ut=9_160_396.76 + i) for i in range(3)])
        self.assertIn(mlib.B5_ORBIT, state.phases_reached)
        self.assertNotIn(mlib.B5_MJ_ASCENT, state.phases_reached)
        self.assertNotIn(mlib.B5_CIRCULARIZE, state.phases_reached)

    def test_a_measurable_gate_that_never_passes_assert_fails(self):
        # DETERMINISTIC: a retry re-loads the same fixture and reads the same
        # numbers, so this must NOT be a flake.
        frames = [parked_snap(ut=9_160_396.76 + i * 40.0, situation="LANDED")
                  for i in range(5)]
        state, _ = self._run(frames)
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("orbit-start entry gate never met", state.loss_reason)
        self.assertIn("LANDED", state.loss_reason)

    def test_telemetry_that_never_settles_flakes_instead(self):
        # TRANSIENT: nothing was ever judged, so the honest verdict is a flake
        # (retry per policy), not a fixture verdict.
        frames = [parked_snap(ut=9_160_396.76 + i * 40.0, body="")
                  for i in range(5)]
        state, _ = self._run(frames)
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertIn("orbit-start telemetry never settled", state.flake_reason)

    def test_the_settle_clock_anchors_on_the_first_frame_not_the_phase_entry(self):
        # THE TRAP THIS GUARDS. PRELAUNCH is untimed (_b5_phase_budget returns
        # None) and B5State.phase_entry_ut defaults to 0.0, so any budget
        # comparison against it on a fixture at UT ~9.16e6 reads as an instant
        # 9-million-second overrun. The branch must carry its own anchor.
        state, _ = mlib.b5_decide(self.state, parked_snap(situation="LANDED"))
        self.assertFalse(state.done)
        self.assertEqual(state.start_in_orbit_anchor_ut, 9_160_396.76)
        self.assertEqual(state.phase_entry_ut, 0.0)


class OrbitStartInertnessTests(unittest.TestCase):
    """THE LOAD-BEARING HALF. Opening a second PRELAUNCH door must move NOTHING
    on the twenty-plus flown lanes that use the first one. Mirrors
    test_b17_duna_direct.py's `test_flag_off_prelaunch_is_the_proven_immediate_
    launch`, and widens it: same phase, same actions, same values, on the
    plain-launch shape AND on the pad-align shape."""

    def test_flag_off_prelaunch_is_the_proven_immediate_launch(self):
        params = mlib.b5_params_from_dict({
            "targetApoapsisMeters": 80000, "targetBodyName": "Mun",
            "homeBodyName": "Kerbin"})
        state, actions = mlib.b5_decide(
            mlib.b5_initial_state(params), snap(ut=1000.0))
        self.assertEqual(state.phase, mlib.B5_MJ_ASCENT)
        self.assertEqual(state.launch_ut, 1000.0)
        self.assertEqual([a.kind for a in actions], [
            mlib.ACTION_MJ_SET_TARGET_APOAPSIS, mlib.ACTION_MJ_ENABLE_AUTOSTAGE,
            mlib.ACTION_MJ_ENGAGE_ASCENT, mlib.ACTION_ACTIVATE_STAGE])

    def test_flag_off_launches_from_a_frame_the_gate_would_reject(self):
        # The sharpest inertness statement available: a REAL pad frame
        # (PRE_LAUNCH, on Kerbin, negative apoapsis) fails every conjunct of the
        # orbit-start gate. With the flag off the machine must not consult it --
        # it must launch, exactly as it always has.
        params = mlib.b5_params_from_dict({
            "targetApoapsisMeters": 80000, "targetBodyName": "Mun",
            "homeBodyName": "Kerbin"})
        pad = snap(ut=1000.0, body="Kerbin", situation="PRE_LAUNCH",
                   apoapsis=-1.0, periapsis=-600000.0, eccentricity=0.9948,
                   altitude=70.0)
        state, actions = mlib.b5_decide(mlib.b5_initial_state(params), pad)
        self.assertEqual(state.phase, mlib.B5_MJ_ASCENT)
        self.assertIsNone(state.verdict)
        self.assertIn(mlib.ACTION_MJ_ENGAGE_ASCENT, [a.kind for a in actions])

    def test_flag_off_pad_align_still_issues_its_one_seam_timejump(self):
        # The OTHER flag-gated PRELAUNCH branch is checked SECOND now. It must
        # still be reached byte-identically when orbit-start is off.
        params = mlib.b5_params_from_dict({
            "targetApoapsisMeters": 100000, "targetBodyName": "Duna",
            "homeBodyName": "Kerbin", "interplanetaryTransfer": True,
            "padAlignEjection": True})
        state, actions = mlib.b5_decide(
            mlib.b5_initial_state(params), snap(ut=1000.0))
        self.assertEqual(state.phase, mlib.B5_PAD_ALIGN)
        self.assertEqual(len(actions), 1)
        self.assertEqual(actions[0].seam_verb, "TimeJump")

    def test_no_assertion_row_is_added_for_a_flag_off_lane(self):
        params = mlib.b5_params_from_dict({
            "targetApoapsisMeters": 80000, "targetBodyName": "Mun",
            "homeBodyName": "Kerbin"})
        rows = mlib.evaluate_b5_assertions(
            (), params, phases_reached=(mlib.B5_ORBIT,),
            state=mlib.b5_initial_state(params))
        self.assertNotIn("startedInHomeOrbit", [r.name for r in rows])

    def test_prelaunch_stays_an_untimed_phase(self):
        # The settle bound is the branch's OWN clock; it must not have leaked
        # into the shared phase-budget table, where it would newly time-bound
        # PRELAUNCH for every pad lane.
        params = mlib.b5_params_from_dict(b23_params())
        self.assertIsNone(mlib._b5_phase_budget(params, mlib.B5_PRELAUNCH))


class StartedInHomeOrbitRowTests(unittest.TestCase):
    def _rows(self, **fields):
        params = mlib.b5_params_from_dict(b23_params())
        state = replace(mlib.b5_initial_state(params), **fields)
        phases = fields.pop("_phases", (mlib.B5_ORBIT,))
        return {r.name: r for r in mlib.evaluate_b5_assertions(
            (), params, phases_reached=phases, state=state)}

    def test_met_on_the_committed_fixtures_park(self):
        row = self._rows(start_in_orbit_entry_apoapsis=719_680.0,
                         start_in_orbit_entry_periapsis=717_047.0,
                         start_in_orbit_entry_eccentricity=0.0012696,
                         launch_ut=9_160_398.76)["startedInHomeOrbit"]
        self.assertTrue(row.met)
        self.assertEqual(row.detail["body"], "Duna")

    def test_unmet_without_the_entry_stamps_fail_closed(self):
        # A machine that somehow reached ORBIT without passing the gate must not
        # be able to report met.
        row = self._rows()["startedInHomeOrbit"]
        self.assertFalse(row.met)

    def test_unmet_when_orbit_was_never_reached(self):
        params = mlib.b5_params_from_dict(b23_params())
        state = replace(mlib.b5_initial_state(params),
                        start_in_orbit_entry_apoapsis=719_680.0,
                        start_in_orbit_entry_periapsis=717_047.0,
                        start_in_orbit_entry_eccentricity=0.0012696)
        rows = {r.name: r for r in mlib.evaluate_b5_assertions(
            (), params, phases_reached=(mlib.B5_PRELAUNCH,), state=state)}
        self.assertFalse(rows["startedInHomeOrbit"].met)

    def test_unmet_on_a_hyperbolic_entry_stamp(self):
        row = self._rows(start_in_orbit_entry_apoapsis=-4_000_000.0,
                         start_in_orbit_entry_periapsis=717_047.0,
                         start_in_orbit_entry_eccentricity=0.0012696
                         )["startedInHomeOrbit"]
        self.assertFalse(row.met)


class SpecArithmeticTests(unittest.TestCase):
    """Cells that read the COMMITTED spec, so a parameter edit that breaks one
    of the derivations in its header reds here instead of on the flight.

    These are the b17 `test_the_window_guard_headroom_arithmetic_still_holds`
    pattern: the spec's prose states an arithmetic relationship between its own
    values, and the relationship is checked rather than trusted."""

    @classmethod
    def setUpClass(cls):
        path = os.path.join(HARNESS_ROOT, "scenarios", "B23-ike-orbit.toml")
        with open(path, "rb") as fh:
            cls.spec = tomllib.load(fh)
        cls.mp = cls.spec["driver"]["missionParams"]

    # Ike's SOI-entry -> periapsis coast, solved on the approach hyperbola in the
    # spec header: 4,328 game s to a 100 km periapsis, 4,446 to a 150 km one. The
    # sizing uses a PESSIMISTIC floor that sits under the whole band on purpose.
    IKE_APPROACH_COAST_FLOOR_SECONDS = 3000.0

    def test_one_frame_cannot_swallow_the_ike_approach(self):
        # B19's rule, carried as arithmetic: one poll frame at the approach
        # ceiling must advance well under a FIFTH of the SOI-entry -> periapsis
        # coast. Ike needs factor 4 (x100) for a reason different from Moho's --
        # its gravity DOMINATES the approach (2*mu/r_soi = 35,381 against
        # v_inf^2 = 8,482) rather than being negligible -- but the bound is the
        # same shape.
        rate = mlib.RAILS_WARP_RATES[self.mp["approachMaxWarpFactor"]]
        self.assertLessEqual(rate, self.IKE_APPROACH_COAST_FLOOR_SECONDS / 5.0,
                             "the approach ceiling can swallow Ike's SOI-entry "
                             "-> periapsis coast in one poll")

    def test_the_soi_lead_exceeds_one_coast_frame(self):
        # The OTHER half of the B19 pair, and the half B19 flight 4 lost: the
        # lead must be expressible in units of COAST WARP FRAMES. This lane's
        # coast rate is x100, so a conservative 4 s poll advances ~400 game s.
        coast_rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        one_frame = coast_rate * 4.0
        self.assertGreaterEqual(self.mp["soiLeadSeconds"], 3.0 * one_frame,
                                "soiLeadSeconds is under 3 coast frames; one "
                                "poll can blow through the boundary")

    def test_the_approach_window_covers_the_lead(self):
        # The ceiling must already be in force when the native warp hands off,
        # or the clamp arms after the overshoot it exists to prevent (B20's 2x
        # ratio).
        self.assertGreaterEqual(self.mp["approachWindowSeconds"],
                                2.0 * self.mp["soiLeadSeconds"])

    def test_the_transfer_burn_budget_covers_a_whole_synodic_period(self):
        # The NodeExecutor autowarps to a node up to ONE synodic period ahead.
        # DERIVED from the stock constants rather than quoted, so a wrong
        # comment cannot make this pass.
        mu = 3.0136321e11          # Duna
        r_park = 1_038_215.0       # the fixture's measured SMA
        a_ike = 3_200_000.0
        import math
        t_park = 2 * math.pi * math.sqrt(r_park ** 3 / mu)
        t_ike = 2 * math.pi * math.sqrt(a_ike ** 3 / mu)
        synodic = 1.0 / abs(1.0 / t_park - 1.0 / t_ike)
        self.assertAlmostEqual(synodic, 14_850.0, delta=100.0)
        self.assertGreaterEqual(self.mp["transferBurnTimeoutSeconds"],
                                2.0 * synodic)

    def test_the_coast_budget_covers_the_transfer_several_times_over(self):
        import math
        mu = 3.0136321e11
        a_t = (1_038_215.0 + 3_200_000.0) / 2.0
        tof = math.pi * math.sqrt(a_t ** 3 / mu)
        self.assertAlmostEqual(tof, 17_650.0, delta=100.0)
        self.assertGreaterEqual(self.mp["coastTimeoutSeconds"], 5.0 * tof)

    def test_the_entry_gate_admits_the_committed_fixtures_measured_park(self):
        # THE cell that couples the spec to the fixture. If either moves, this
        # reds before a KSP boot is spent.
        params = mlib.b5_params_from_dict(self.mp)
        verdict, reason = mlib.start_in_orbit_frame_verdict(params, parked_snap())
        self.assertEqual(verdict, mlib.START_IN_ORBIT_IN_GATE, reason)

    def test_the_entry_ceiling_stays_clear_of_ikes_own_shell(self):
        # A start park allowed up to Ike's orbital altitude (2,880 km) would let
        # the lane begin inside an unplanned encounter.
        self.assertLess(self.mp["startInOrbitMaxApoapsisMeters"], 2_880_000.0)

    def test_the_park_ceiling_stays_inside_ikes_soi(self):
        # Ike SOI 1,049,599 m from the centre, radius 130 km => ~919.6 km of
        # ALTITUDE. A park apoapsis above that is not a park, it is an escape.
        self.assertLess(self.mp["parkMaxApoapsisMeters"], 919_600.0)

    def test_the_correction_cap_is_below_the_transfer_it_corrects(self):
        # A "correction" priced above the whole 123 m/s hop is the wrong plan
        # (B15 flight 3), and the cap is what makes the machine discard it.
        self.assertLess(self.mp["maxCorrectionDvMps"], 123.0)

    def test_the_spec_arms_no_gating_block(self):
        # A first-flight READING lane must arm nothing: the ARMED_ALLOWLIST in
        # harness/lib/test_hlib.py owns that decision and this spec is not on
        # it. Guarded here too so the intent is stated where the lane lives.
        for block in ("rewind", "recordings"):
            sub = (self.spec.get("expectations") or {}).get(block) or {}
            self.assertNotIn("gating", sub)
            for nested in sub.values():
                if isinstance(nested, dict):
                    self.assertNotIn("gating", nested)


if __name__ == "__main__":
    unittest.main()
