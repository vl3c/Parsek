"""Unit tests for mlib.py, the pure mission-side decision library of M-B1.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

Each test names the regression it guards (design Test Plan "Pure unit tests").
The phase machines are driven over SCRIPTED telemetry snapshots; the assertion
evaluators over hand-built frame sequences; nothing here imports krpc or touches
the venv, so the whole suite runs on the base interpreter.
"""

import math
import unittest

import mlib
from mlib import Action, TelemetrySnapshot


def snap(**kw):
    """Terse TelemetrySnapshot builder (defaults are benign: fuel present, not
    descending, 1x warp)."""
    return TelemetrySnapshot(**kw)


B1_PARAMS = mlib.B1Params(
    throttle=1.0,
    chute_deploy_alt=2500.0,
    ascent_timeout=90.0,
    coast_timeout=180.0,
    descent_timeout=240.0,
    landed_situations=("LANDED", "SPLASHED"),
    apoapsis_window=(6000.0, 30000.0),
)

B2_PARAMS = mlib.B2Params(
    target_apoapsis=80000.0,
    target_periapsis=80000.0,
    apo_error=5000.0,
    peri_error=5000.0,
    eccentricity_max=0.02,
    inclination_error=2.0,
    ascent_timeout=420.0,
    circularize_timeout=300.0,
    launch_site_latitude=0.0,
)


def drive_b1(state, frames):
    """Feed a list of snapshots through b1_decide, returning
    (final_state, [actions_per_frame])."""
    per_frame = []
    for f in frames:
        state, actions = mlib.b1_decide(state, f)
        per_frame.append(actions)
    return state, per_frame


def drive_b2(state, frames):
    per_frame = []
    for f in frames:
        state, actions = mlib.b2_decide(state, f)
        per_frame.append(actions)
    return state, per_frame


def _b1_params_with_limit(limit):
    """B1_PARAMS with a custom frozen_sample_limit (frozen dataclasses expose
    __dict__, the spread pattern the existing tests use)."""
    return B1_PARAMS.__class__(**{**B1_PARAMS.__dict__, "frozen_sample_limit": limit})


def _b2_params_with_limit(limit):
    return B2_PARAMS.__class__(**{**B2_PARAMS.__dict__, "frozen_sample_limit": limit})


def _b1_descent_state(params, **overrides):
    """A B1State pinned in DESCENT for the frozen-telemetry tests."""
    base = mlib.b1_initial_state(params)
    fields = {**base.__dict__, "phase": mlib.B1_DESCENT, "phase_entry_ut": 0.0}
    fields.update(overrides)
    return base.__class__(**fields)


# Bit-identical (dead/stale-vessel) fields a frozen run repeats while UT advances.
_FROZEN_FIELDS = dict(altitude=1000.0, vertical_speed=-5.0, apoapsis=14000.0, periapsis=500.0)


def _frozen_frames(n, start_ut=1.0, **field_overrides):
    """``n`` snapshots with advancing UT and IDENTICAL other fields -- the stalled
    dead-vessel telemetry the detector must catch."""
    fields = {**_FROZEN_FIELDS, **field_overrides}
    return [snap(ut=start_ut + i, **fields) for i in range(n)]


# ---------------------------------------------------------------------------
# B1 phase state machine.
# ---------------------------------------------------------------------------


class B1MachineTests(unittest.TestCase):
    """Guards the B1 pad-hop machine (design "Mission B1"): the machine must
    throttle+stage at start, cut throttle only when fuel is spent, deploy the
    chute only on descent below the threshold, and detect landing -- a mis-wired
    transition (early throttle cut, chute during ascent, missed landing) would
    hang a real mission to its budget and flake."""

    def test_prelaunch_emits_throttle_and_stage_then_ascent(self):
        # Regression: the FIRST decision must throttle up and activate the stage
        # (the launch transition auto-record hangs off), and enter ASCENT.
        state = mlib.b1_initial_state(B1_PARAMS)
        new, actions = mlib.b1_decide(state, snap(ut=0.0))
        self.assertEqual(new.phase, mlib.B1_ASCENT)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 1.0),
                                   Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(new.phase_entry_ut, 0.0)

    def test_ascent_holds_until_fuel_exhausted_then_cuts_throttle(self):
        # Regression: throttle must NOT be cut while fuel is burning (an early cut
        # would abort the hop short of the apoapsis window).
        state = mlib.b1_initial_state(B1_PARAMS)
        state, _ = mlib.b1_decide(state, snap(ut=0.0))
        # fuel still present -> stay in ASCENT, no actions
        state, actions = mlib.b1_decide(state, snap(ut=1.0, stage_solid_fuel=50.0))
        self.assertEqual(state.phase, mlib.B1_ASCENT)
        self.assertEqual(actions, [])
        # fuel exhausted -> cut throttle, enter COAST
        state, actions = mlib.b1_decide(state, snap(ut=5.0, stage_solid_fuel=0.0))
        self.assertEqual(state.phase, mlib.B1_COAST)
        self.assertEqual(actions, [Action(mlib.ACTION_CUT_THROTTLE, 0.0)])

    def test_full_happy_path_transitions_and_chute(self):
        # Regression: the whole PRELAUNCH->...->LANDED walk with the chute deployed
        # exactly once, below the deploy altitude, on the way down.
        state = mlib.b1_initial_state(B1_PARAMS)
        frames = [
            snap(ut=0.0),                                         # PRELAUNCH->ASCENT
            snap(ut=2.0, stage_solid_fuel=10.0, apoapsis=5000.0),  # ASCENT
            snap(ut=6.0, stage_solid_fuel=0.0, apoapsis=14000.0),  # ASCENT->COAST
            snap(ut=10.0, vertical_speed=20.0, apoapsis=14200.0),  # COAST (rising)
            snap(ut=20.0, vertical_speed=-5.0, apoapsis=14210.0),  # COAST->DESCENT
            snap(ut=30.0, altitude=5000.0, vertical_speed=-40.0),  # DESCENT (above chute alt)
            snap(ut=40.0, altitude=2000.0, vertical_speed=-30.0),  # DESCENT->chute
            snap(ut=55.0, altitude=0.0, situation="LANDED"),       # DESCENT->LANDED
        ]
        state, per_frame = drive_b1(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B1_LANDED)
        self.assertIsNone(state.verdict)
        self.assertEqual(state.peak_apoapsis, 14210.0)
        # chute deployed exactly once, at the frame below the 2500 m threshold
        self.assertEqual(per_frame[6], [Action(mlib.ACTION_DEPLOY_CHUTE)])
        chute_frames = [i for i, acts in enumerate(per_frame)
                        if Action(mlib.ACTION_DEPLOY_CHUTE) in acts]
        self.assertEqual(chute_frames, [6])
        self.assertEqual(state.phases_reached,
                         (mlib.B1_PRELAUNCH, mlib.B1_ASCENT, mlib.B1_COAST,
                          mlib.B1_DESCENT, mlib.B1_LANDED))

    def test_chute_not_deployed_above_threshold(self):
        # Regression: the chute must never deploy while still high (a high deploy
        # rips the chute / distorts the recorded descent).
        state = mlib.b1_initial_state(B1_PARAMS)
        state = state.__class__(**{**state.__dict__, "phase": mlib.B1_DESCENT,
                                   "phase_entry_ut": 0.0})
        _, actions = mlib.b1_decide(state, snap(ut=1.0, altitude=9000.0, vertical_speed=-40.0))
        self.assertEqual(actions, [])

    def test_ascent_budget_overrun_flakes_naming_phase(self):
        # Regression: a stuck ASCENT must FLAKE (naming the phase), never hang.
        state = mlib.b1_initial_state(B1_PARAMS)
        state, _ = mlib.b1_decide(state, snap(ut=0.0))
        # fuel never depletes; ut passes the 90 s ascent budget
        state, actions = mlib.b1_decide(state, snap(ut=200.0, stage_solid_fuel=50.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.B1_ASCENT)
        self.assertEqual(actions, [])

    def test_done_state_is_idempotent(self):
        # Regression: once LANDED the machine must be inert (no repeated actions).
        state = mlib.b1_initial_state(B1_PARAMS)
        landed = state.__class__(**{**state.__dict__, "phase": mlib.B1_LANDED, "done": True})
        new, actions = mlib.b1_decide(landed, snap(ut=999.0))
        self.assertIs(new, landed)
        self.assertEqual(actions, [])

    def test_nan_ut_does_not_trip_budget(self):
        # Regression: a NaN UT frame must not spuriously flake a phase (the outer
        # watchdog is the backstop, not a NaN clock).
        state = mlib.b1_initial_state(B1_PARAMS)
        state, _ = mlib.b1_decide(state, snap(ut=0.0))
        state, _ = mlib.b1_decide(state, snap(ut=float("nan"), stage_solid_fuel=50.0))
        self.assertEqual(state.phase, mlib.B1_ASCENT)
        self.assertIsNone(state.verdict)


# ---------------------------------------------------------------------------
# B2 phase state machine.
# ---------------------------------------------------------------------------


class B2MachineTests(unittest.TestCase):
    """Guards the B2 LKO-ascent machine (design "Mission B2"): engage MechJeb at
    start, leave MJ-ASCENT only when apoapsis reaches target, and reach ORBIT only
    when periapsis is raised -- asserting orbit before circularization, or never
    leaving MJ-ASCENT, would false-flake or false-pass."""

    def test_prelaunch_engages_mechjeb_and_launches(self):
        # The trailing ACTIVATE_STAGE is load-bearing: MechJeb's AscentAutopilot
        # engaged via kRPC does NOT ignite the first stage itself (first live B2
        # run 2026-07-20 sat in PRE_LAUNCH for the whole ascent budget).
        state = mlib.b2_initial_state(B2_PARAMS)
        new, actions = mlib.b2_decide(state, snap(ut=0.0))
        self.assertEqual(new.phase, mlib.B2_MJ_ASCENT)
        self.assertEqual(actions, [
            Action(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, 80000.0),
            Action(mlib.ACTION_MJ_ENABLE_AUTOSTAGE),
            Action(mlib.ACTION_MJ_ENGAGE_ASCENT),
            Action(mlib.ACTION_ACTIVATE_STAGE),
        ])

    def test_full_happy_path(self):
        # Regression: MJ-ASCENT holds until the autopilot LATCHES complete AND
        # apoapsis is at target (apoapsis alone fired mid-burn on the first live
        # B2 run and executed an empty node list), CIRCULARIZE holds until
        # periapsis at target, then ORBIT.
        state = mlib.b2_initial_state(B2_PARAMS)
        frames = [
            snap(ut=0.0),                                       # PRELAUNCH->MJ-ASCENT
            snap(ut=60.0, apoapsis=40000.0),                    # MJ-ASCENT (climbing)
            snap(ut=180.0, apoapsis=78000.0, mj_ascent_complete=True),  # ->CIRCULARIZE (latched + >=75000)
            snap(ut=200.0, apoapsis=80000.0, periapsis=20000.0),  # CIRCULARIZE (pe rising)
            snap(ut=260.0, apoapsis=80000.0, periapsis=79000.0),  # CIRCULARIZE->ORBIT (>=75000)
        ]
        state, per_frame = drive_b2(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B2_ORBIT)
        self.assertIsNone(state.verdict)
        self.assertEqual(per_frame[2], [Action(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION)])
        self.assertEqual(state.phases_reached,
                         (mlib.B2_PRELAUNCH, mlib.B2_MJ_ASCENT,
                          mlib.B2_CIRCULARIZE, mlib.B2_ORBIT))

    def test_does_not_leave_mj_ascent_before_apoapsis(self):
        # Regression: must not enter CIRCULARIZE while apoapsis is short of target.
        state = mlib.b2_initial_state(B2_PARAMS)
        state, _ = mlib.b2_decide(state, snap(ut=0.0))
        state, actions = mlib.b2_decide(state, snap(ut=60.0, apoapsis=60000.0))
        self.assertEqual(state.phase, mlib.B2_MJ_ASCENT)
        self.assertEqual(actions, [])

    def test_mechjeb_stall_flakes_naming_phase(self):
        # Regression: a MechJeb node stall (catalog 5.5) must FLAKE, not hang.
        state = mlib.b2_initial_state(B2_PARAMS)
        state, _ = mlib.b2_decide(state, snap(ut=0.0))
        state, actions = mlib.b2_decide(state, snap(ut=500.0, apoapsis=40000.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.B2_MJ_ASCENT)


# ---------------------------------------------------------------------------
# Assertion evaluators: met / unmet / boundary / NaN.
# ---------------------------------------------------------------------------


class B1AssertionTests(unittest.TestCase):
    """Guards B1 assertions (design): a WINDOW (not golden) apoapsis, inclusive
    boundaries, NaN never a passing compare, and the final-situation check."""

    def _apo_frames(self, ap):
        return [snap(apoapsis=ap) for _ in range(4)]

    def test_apoapsis_in_window_met(self):
        outs = mlib.evaluate_b1_assertions(self._apo_frames(14000.0), B1_PARAMS)
        apo = outs[0]
        self.assertEqual(apo.name, "apoapsisWindow")
        self.assertTrue(apo.met)
        self.assertEqual(apo.value, 14000.0)
        self.assertEqual(apo.detail["window"], [6000.0, 30000.0])

    def test_apoapsis_outside_window_unmet(self):
        # Just above the max -> UNMET (a near-miss must not silently pass).
        outs = mlib.evaluate_b1_assertions(self._apo_frames(35000.0), B1_PARAMS)
        self.assertFalse(outs[0].met)
        self.assertEqual(outs[0].value, 35000.0)

    def test_apoapsis_boundary_inclusive(self):
        # Exactly on the max boundary -> MET (inclusive).
        outs = mlib.evaluate_b1_assertions(self._apo_frames(30000.0), B1_PARAMS)
        self.assertTrue(outs[0].met)
        # Exactly on the min boundary -> MET.
        outs = mlib.evaluate_b1_assertions(self._apo_frames(6000.0), B1_PARAMS)
        self.assertTrue(outs[0].met)

    def test_apoapsis_all_nan_unmet_value_none(self):
        # Regression: a phase producing only NaN never passes; value scrubs to
        # JSON null (never emitted as NaN).
        frames = [snap(apoapsis=float("nan")) for _ in range(4)]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertFalse(outs[0].met)
        self.assertIsNone(outs[0].to_dict()["value"])

    def test_peak_apoapsis_in_window_met(self):
        # BLOCKER-2: the PEAK apoapsis (running max) inside the window -> MET, with
        # the peak reported as the value. The apoapsis climbs then eases back but
        # never exceeds the window's max.
        frames = [snap(apoapsis=a) for a in (7000.0, 12000.0, 15000.0, 14000.0)]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertTrue(outs[0].met)
        self.assertEqual(outs[0].value, 15000.0)  # the peak, not the last reading

    def test_climb_through_window_but_peak_overshoots_is_unmet(self):
        # BLOCKER-2 regression (the self-contradictory row the old frames-in-window
        # debounce produced): a hop that climbs THROUGH the 6-30 km window (frames at
        # 7/15/25 km, K-consecutive in-window on the way UP) but PEAKS at 45 km must
        # be UNMET -- the peak (45 km) lies outside the window. The reported value is
        # that peak, so met=True with value=45000 can never happen.
        frames = [snap(apoapsis=a) for a in (7000.0, 15000.0, 25000.0,
                                             45000.0, 45000.0, 45000.0)]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertFalse(outs[0].met, "a hop peaking above the window must be UNMET")
        self.assertEqual(outs[0].value, 45000.0, "the reported value is the peak")

    def test_peak_ignores_nan_frames(self):
        # A transient NaN apoapsis frame is filtered out of the running max (never
        # inflates or passes the peak); the finite peak still decides the window.
        frames = [snap(apoapsis=7000.0), snap(apoapsis=float("nan")),
                  snap(apoapsis=15000.0), snap(apoapsis=float("inf"))]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertTrue(outs[0].met)
        self.assertEqual(outs[0].value, 15000.0)

    def test_landed_situation_final_frame(self):
        frames = [snap(situation="FLYING"), snap(situation="LANDED")]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        sit = outs[1]
        self.assertEqual(sit.name, "landedSituation")
        self.assertTrue(sit.met)
        self.assertEqual(sit.value, "LANDED")
        self.assertEqual(sit.detail["accepted"], ["LANDED", "SPLASHED"])

    def test_landed_situation_wrong_unmet(self):
        frames = [snap(situation="FLYING")]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertFalse(outs[1].met)


class B2AssertionTests(unittest.TestCase):
    """Guards B2 orbit assertions (design): all tolerance bands (never golden),
    inclusive, NaN never passes, and eccentricity <= max."""

    def _orbit_frames(self, **kw):
        base = dict(apoapsis=80000.0, periapsis=80000.0, eccentricity=0.0, inclination=0.0)
        base.update(kw)
        return [snap(**base) for _ in range(4)]

    def test_all_met(self):
        outs = mlib.evaluate_b2_assertions(self._orbit_frames(), B2_PARAMS)
        self.assertTrue(mlib.all_assertions_met(outs))
        names = [o.name for o in outs]
        self.assertEqual(names, ["apoapsisError", "periapsisError",
                                 "eccentricity", "inclinationError"])

    def test_apoapsis_boundary_inclusive(self):
        # target 80000 +/- 5000: exactly 85000 -> MET; 85001 -> UNMET.
        outs = mlib.evaluate_b2_assertions(self._orbit_frames(apoapsis=85000.0), B2_PARAMS)
        self.assertTrue(outs[0].met)
        outs = mlib.evaluate_b2_assertions(self._orbit_frames(apoapsis=85001.0), B2_PARAMS)
        self.assertFalse(outs[0].met)

    def test_eccentricity_boundary_inclusive(self):
        outs = mlib.evaluate_b2_assertions(self._orbit_frames(eccentricity=0.02), B2_PARAMS)
        self.assertTrue(outs[2].met)
        outs = mlib.evaluate_b2_assertions(self._orbit_frames(eccentricity=0.03), B2_PARAMS)
        self.assertFalse(outs[2].met)

    def test_inclination_uses_launch_site_latitude(self):
        # |inc - 0| <= 2 -> 1.5 met, 3.0 unmet.
        outs = mlib.evaluate_b2_assertions(self._orbit_frames(inclination=1.5), B2_PARAMS)
        self.assertTrue(outs[3].met)
        outs = mlib.evaluate_b2_assertions(self._orbit_frames(inclination=3.0), B2_PARAMS)
        self.assertFalse(outs[3].met)

    def test_nan_periapsis_unmet(self):
        outs = mlib.evaluate_b2_assertions(self._orbit_frames(periapsis=float("inf")), B2_PARAMS)
        self.assertFalse(outs[1].met)
        self.assertIsNone(outs[1].to_dict()["value"])


# ---------------------------------------------------------------------------
# Debounce over noisy telemetry.
# ---------------------------------------------------------------------------


class DebounceTests(unittest.TestCase):
    """Guards the K-consecutive debounce (design "Determinism guardrails" / edge
    11): a single warp-edge outlier / NaN frame among in-tolerance frames must NOT
    fail an otherwise-met assertion, and a persistent out-of-tolerance run MUST
    fail. A regression here either false-fails a good flight (MechJeb stutter) or
    masks a real miss.

    These exercise the debounce through B2's ``apoapsisError`` (outs[0]) -- a B2
    orbit param IS a per-frame terminal-state window that keeps the K-consecutive
    debounce. B1's apoapsisWindow is NO LONGER a debounced per-frame check (it is
    the running-PEAK gate, BLOCKER-2), so a B1 apoapsis "outlier" is a real overshoot
    and is covered by B1AssertionTests, not here. B2's apoapsis window is
    target +/- apo_error = [75000, 85000]."""

    def test_single_outlier_tolerated(self):
        # One out-of-window frame between good frames -> still MET (a later run of
        # K=3 in-window frames settles it).
        frames = [snap(apoapsis=80000.0), snap(apoapsis=80000.0),
                  snap(apoapsis=999999.0),  # warp-edge outlier
                  snap(apoapsis=80000.0), snap(apoapsis=80000.0), snap(apoapsis=80000.0)]
        outs = mlib.evaluate_b2_assertions(frames, B2_PARAMS)
        self.assertTrue(outs[0].met)

    def test_single_nan_frame_tolerated(self):
        frames = [snap(apoapsis=80000.0), snap(apoapsis=80000.0),
                  snap(apoapsis=float("nan")),
                  snap(apoapsis=80000.0), snap(apoapsis=80000.0), snap(apoapsis=80000.0)]
        outs = mlib.evaluate_b2_assertions(frames, B2_PARAMS)
        self.assertTrue(outs[0].met)

    def test_persistent_out_of_tolerance_fails(self):
        # A sustained out-of-window run never reaches K consecutive in-window.
        frames = [snap(apoapsis=40000.0) for _ in range(6)]
        outs = mlib.evaluate_b2_assertions(frames, B2_PARAMS)
        self.assertFalse(outs[0].met)

    def test_scattered_good_frames_never_settle(self):
        # Alternating in/out never yields K=3 consecutive -> UNMET (a real miss
        # peeking through warp noise must not be masked).
        frames = [snap(apoapsis=80000.0), snap(apoapsis=40000.0),
                  snap(apoapsis=80000.0), snap(apoapsis=40000.0),
                  snap(apoapsis=80000.0), snap(apoapsis=40000.0)]
        outs = mlib.evaluate_b2_assertions(frames, B2_PARAMS)
        self.assertFalse(outs[0].met)

    def test_has_k_consecutive_primitive(self):
        self.assertTrue(mlib._has_k_consecutive_true([False, True, True, True], 3))
        self.assertFalse(mlib._has_k_consecutive_true([True, True, False, True], 3))
        # k<=0 clamps to 1 (an empty sequence still fails).
        self.assertFalse(mlib._has_k_consecutive_true([], 0))
        self.assertTrue(mlib._has_k_consecutive_true([True], 0))

    def test_custom_k_of_one(self):
        # With k=1 a single in-window frame suffices.
        outs = mlib.evaluate_b2_assertions([snap(apoapsis=80000.0)], B2_PARAMS, k=1)
        self.assertTrue(outs[0].met)


# ---------------------------------------------------------------------------
# Connect-retry decision.
# ---------------------------------------------------------------------------


class ConnectRetryTests(unittest.TestCase):
    """Guards decide_connect_retry (design edge 1): RETRY only while BOTH bounds
    have room, TIMEOUT past EITHER -- a slow bind must be retried (no spurious
    connect-timeout), an unreachable server must not retry forever (no unbounded
    wait)."""

    def test_retry_within_both_bounds(self):
        self.assertEqual(mlib.decide_connect_retry(5.0, 2, 30.0, 10), mlib.CONNECT_RETRY)

    def test_timeout_past_budget(self):
        self.assertEqual(mlib.decide_connect_retry(30.0, 2, 30.0, 10), mlib.CONNECT_TIMEOUT)
        self.assertEqual(mlib.decide_connect_retry(31.0, 2, 30.0, 10), mlib.CONNECT_TIMEOUT)

    def test_timeout_past_attempts(self):
        self.assertEqual(mlib.decide_connect_retry(5.0, 10, 30.0, 10), mlib.CONNECT_TIMEOUT)
        self.assertEqual(mlib.decide_connect_retry(5.0, 11, 30.0, 10), mlib.CONNECT_TIMEOUT)

    def test_boundary_just_under_budget_retries(self):
        self.assertEqual(mlib.decide_connect_retry(29.999, 9, 30.0, 10), mlib.CONNECT_RETRY)

    def test_non_finite_elapsed_times_out(self):
        self.assertEqual(mlib.decide_connect_retry(float("nan"), 1, 30.0, 10),
                         mlib.CONNECT_TIMEOUT)


# ---------------------------------------------------------------------------
# Flight verdict resolution (machine + assertions -> verdict).
# ---------------------------------------------------------------------------


class FlightVerdictTests(unittest.TestCase):
    """Guards resolve_flight_verdict: all met -> MISSION-OK, any unmet ->
    MISSION-ASSERT-FAIL, a phase timeout -> MISSION-FLAKE (design)."""

    def test_all_met_is_ok(self):
        outs = mlib.evaluate_b2_assertions(
            [snap(apoapsis=80000.0, periapsis=80000.0, eccentricity=0.0, inclination=0.0)
             for _ in range(4)], B2_PARAMS)
        state = mlib.b2_initial_state(B2_PARAMS)
        verdict, _ = mlib.resolve_flight_verdict(state, outs)
        self.assertEqual(verdict, mlib.MISSION_OK)

    def test_unmet_is_assert_fail(self):
        outs = mlib.evaluate_b1_assertions([snap(apoapsis=40000.0, situation="LANDED")
                                            for _ in range(4)], B1_PARAMS)
        state = mlib.b1_initial_state(B1_PARAMS)
        verdict, reason = mlib.resolve_flight_verdict(state, outs)
        self.assertEqual(verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("apoapsisWindow", reason)

    def test_flake_wins(self):
        state = mlib.b1_initial_state(B1_PARAMS)
        flaked = state.__class__(**{**state.__dict__, "verdict": mlib.MISSION_FLAKE,
                                    "flake_phase": mlib.B1_ASCENT, "done": True})
        verdict, reason = mlib.resolve_flight_verdict(flaked, [])
        self.assertEqual(verdict, mlib.MISSION_FLAKE)
        self.assertIn("ASCENT", reason)


# ---------------------------------------------------------------------------
# Physics-warp guard (design edge 7).
# ---------------------------------------------------------------------------


class WarpGuardTests(unittest.TestCase):
    """Guards is_unexpected_warp (design edge 7): PHYSICS warp is never permitted;
    RAILS warp only when allow_rails (B2 exo coast); 1x is always fine. A regression
    here either false-flakes a clean 1x run or lets a physics-warped (distorted)
    flight record silently."""

    def test_one_x_is_never_unexpected(self):
        # rate<=1 -> fine regardless of mode / allow_rails (clean run unaffected).
        self.assertFalse(mlib.is_unexpected_warp(mlib.WARP_NONE, 1.0, False))
        self.assertFalse(mlib.is_unexpected_warp(mlib.WARP_RAILS, 1.0, False))
        self.assertFalse(mlib.is_unexpected_warp(mlib.WARP_PHYSICS, 0.5, True))

    def test_physics_warp_unexpected_by_default(self):
        # PHYSICS warp above 1x is a determinism violation at the default
        # max_physics_warp=0.0 (B1's contract).
        self.assertTrue(mlib.is_unexpected_warp(mlib.WARP_PHYSICS, 4.0, False))
        self.assertTrue(mlib.is_unexpected_warp(mlib.WARP_PHYSICS, 4.0, True))
        self.assertTrue(mlib.is_unexpected_warp(mlib.WARP_PHYSICS, 1.2, False))

    def test_physics_warp_permitted_up_to_bound(self):
        # B2 (max_physics_warp=2.0): MechJeb's own ascent warp (ramping 1.1-2.0x,
        # observed live 2026-07-20) is permitted, including the continuous ramp
        # slightly above the step rate; clearly above the bound still flakes.
        self.assertFalse(mlib.is_unexpected_warp(
            mlib.WARP_PHYSICS, 1.486, False, max_physics_warp=2.0))
        self.assertFalse(mlib.is_unexpected_warp(
            mlib.WARP_PHYSICS, 2.0 + mlib.PHYSICS_WARP_RAMP_ALLOWANCE, False,
            max_physics_warp=2.0))
        self.assertTrue(mlib.is_unexpected_warp(
            mlib.WARP_PHYSICS, 3.0, False, max_physics_warp=2.0))
        # A non-finite bound fails closed (never permits).
        self.assertTrue(mlib.is_unexpected_warp(
            mlib.WARP_PHYSICS, 1.5, False, max_physics_warp=float("nan")))

    def test_rails_warp_gated_by_allow_rails(self):
        # B1 (allow_rails=False) forbids RAILS warp; B2 (True) permits it.
        self.assertTrue(mlib.is_unexpected_warp(mlib.WARP_RAILS, 50.0, False))
        self.assertFalse(mlib.is_unexpected_warp(mlib.WARP_RAILS, 50.0, True))

    def test_unknown_mode_above_1x_is_unexpected(self):
        # Above 1x with a NONE/unknown mode is an inconsistent, unexpected state.
        self.assertTrue(mlib.is_unexpected_warp(mlib.WARP_NONE, 4.0, True))
        self.assertTrue(mlib.is_unexpected_warp("WEIRD", 4.0, True))

    def test_non_finite_rate_is_not_unexpected(self):
        # A NaN rate reading never trips the guard (a transient, not a violation).
        self.assertFalse(mlib.is_unexpected_warp(mlib.WARP_PHYSICS, float("nan"), False))


# ---------------------------------------------------------------------------
# Post-connect exception origin classification (design edges 5 / 8 vs 9).
# ---------------------------------------------------------------------------


class PostConnectExceptionTests(unittest.TestCase):
    """Guards classify_post_connect_exception (design edge 5/8 vs 9): a kRPC-origin
    or connection-drop exception raised AFTER connect is MISSION-FLAKE (retryable
    autopilot-flake), while an internal non-kRPC exception stays MISSION-ERROR. A
    regression either buries a real mission-script bug as a flake, or poisons the
    Parsek-defect bucket by mis-filing a transient drop."""

    def test_krpc_package_exception_is_flake(self):
        self.assertEqual(mlib.MISSION_FLAKE,
                         mlib.classify_post_connect_exception("krpc.error", "RPCError"))
        self.assertEqual(mlib.MISSION_FLAKE,
                         mlib.classify_post_connect_exception("krpc", "ConnectionError"))

    def test_stdlib_connection_drop_name_is_flake(self):
        # A torn socket surfaces as a builtin ConnectionResetError (module builtins).
        self.assertEqual(mlib.MISSION_FLAKE,
                         mlib.classify_post_connect_exception("builtins", "ConnectionResetError"))
        self.assertEqual(mlib.MISSION_FLAKE,
                         mlib.classify_post_connect_exception("builtins", "BrokenPipeError"))

    def test_internal_non_krpc_exception_is_error(self):
        # A None dereference / bad param (edge 9) is a genuine mission-script bug.
        self.assertEqual(mlib.MISSION_ERROR,
                         mlib.classify_post_connect_exception("builtins", "AttributeError"))
        self.assertEqual(mlib.MISSION_ERROR,
                         mlib.classify_post_connect_exception("builtins", "ValueError"))

    def test_none_origin_defaults_to_error(self):
        self.assertEqual(mlib.MISSION_ERROR,
                         mlib.classify_post_connect_exception(None, None))


# ---------------------------------------------------------------------------
# Mission-result round-trip + determinism.
# ---------------------------------------------------------------------------


class MissionResultTests(unittest.TestCase):
    """Guards mission-result serialize/parse/validate (design Data Model): a
    round-trip yields an equal object, identical inputs produce byte-identical
    JSON (stable key order), and a field run.py reads that is dropped/mistyped is
    caught by validate -- a result-diff churn or a silently dropped verdict would
    break the harness handoff."""

    def _sample_result(self):
        apo = mlib.AssertionOutcome("apoapsisWindow", True, 14210.4, {"window": [6000, 30000]})
        sit = mlib.AssertionOutcome("landedSituation", True, "LANDED",
                                    {"accepted": ["LANDED", "SPLASHED"]})
        return mlib.build_mission_result(
            mission="b1_pad_hop",
            verdict=mlib.MISSION_OK,
            reason="landed within apoapsis window",
            phases_reached=[mlib.B1_PRELAUNCH, mlib.B1_ASCENT, mlib.B1_COAST,
                            mlib.B1_DESCENT, mlib.B1_LANDED],
            connect_attempts=2,
            connected_seconds=3.1,
            rpc_port=50000,
            assertions=[apo, sit],
            wall_seconds=71,
            krpc_client_version="0.5.4",
            krpc_server_version="0.5.4",
            error=None,
        )

    def test_round_trip_equal(self):
        result = self._sample_result()
        text = mlib.serialize_mission_result(result)
        parsed = mlib.parse_mission_result(text)
        self.assertEqual(parsed, result)

    def test_validate_accepts_well_formed(self):
        ok, errors = mlib.validate_mission_result(self._sample_result())
        self.assertTrue(ok, errors)
        self.assertEqual(errors, ())

    def test_byte_identical_for_identical_inputs(self):
        a = mlib.serialize_mission_result(self._sample_result())
        b = mlib.serialize_mission_result(self._sample_result())
        self.assertEqual(a, b)
        # Trailing newline + LF endings (no CRLF) so cross-OS output matches.
        self.assertTrue(a.endswith("\n"))
        self.assertNotIn("\r", a)

    def test_key_order_stable(self):
        # sort_keys makes the top-level key order deterministic regardless of the
        # dict insertion order.
        text = mlib.serialize_mission_result(self._sample_result())
        keys = [ln.strip().split('"')[1] for ln in text.splitlines()
                if ln.startswith("  \"")]
        self.assertEqual(keys, sorted(keys))

    def test_validate_rejects_bad_verdict(self):
        result = self._sample_result()
        result["verdict"] = "NOPE"
        ok, errors = mlib.validate_mission_result(result)
        self.assertFalse(ok)
        self.assertTrue(any("verdict" in e for e in errors))

    def test_validate_rejects_wrong_schema(self):
        result = self._sample_result()
        result["schema"] = 2
        ok, errors = mlib.validate_mission_result(result)
        self.assertFalse(ok)
        self.assertTrue(any("schema" in e for e in errors))

    def test_validate_rejects_missing_connect_fields(self):
        result = self._sample_result()
        del result["connect"]["rpcPort"]
        ok, errors = mlib.validate_mission_result(result)
        self.assertFalse(ok)
        self.assertTrue(any("rpcPort" in e for e in errors))

    def test_connect_timeout_result_scrubs_non_finite_seconds(self):
        # A never-connected result carries connectedSeconds=inf -> scrubbed to null
        # so the JSON stays valid + serializable (allow_nan=False).
        result = mlib.build_mission_result(
            mission="b1_pad_hop", verdict=mlib.MISSION_CONNECT_TIMEOUT,
            reason="connect budget exhausted", phases_reached=[],
            connect_attempts=10, connected_seconds=float("inf"), rpc_port=50000,
            assertions=[], wall_seconds=30, krpc_client_version="0.5.4",
            krpc_server_version="", error=None)
        self.assertIsNone(result["connect"]["connectedSeconds"])
        text = mlib.serialize_mission_result(result)  # must not raise
        self.assertEqual(mlib.parse_mission_result(text), result)

    def test_serialize_raises_on_unscrubbed_nan(self):
        # allow_nan=False is the safety net: a raw NaN that slipped past the
        # scrubbers raises instead of emitting invalid JSON.
        result = self._sample_result()
        result["wallSeconds"] = float("nan")
        with self.assertRaises(ValueError):
            mlib.serialize_mission_result(result)

    def test_params_from_dict_round_trip(self):
        # The spec missionParams block parses into the typed params objects.
        p = mlib.b1_params_from_dict({
            "throttle": 1.0,
            "apoapsisWindowMeters": {"min": 6000, "max": 30000},
            "chuteDeployAltMeters": 2500,
            "landedSituations": ["LANDED", "SPLASHED"],
            "ascentTimeoutSeconds": 90,
            "coastTimeoutSeconds": 180,
            "descentTimeoutSeconds": 240,
        })
        self.assertEqual(p.apoapsis_window, (6000.0, 30000.0))
        self.assertEqual(p.landed_situations, ("LANDED", "SPLASHED"))
        q = mlib.b2_params_from_dict({
            "targetApoapsisMeters": 80000, "targetPeriapsisMeters": 80000,
            "apoErrorMeters": 5000, "periErrorMeters": 5000,
            "eccentricityMax": 0.02, "inclinationErrorDeg": 2.0,
            "ascentTimeoutSeconds": 420, "circularizeTimeoutSeconds": 300,
        })
        self.assertEqual(q.target_apoapsis, 80000.0)
        self.assertEqual(q.launch_site_latitude, 0.0)


# ---------------------------------------------------------------------------
# Vessel-lost / frozen-telemetry terminal (design "First live B1 flown-mission
# run": vessel-destroyed terminal).
# ---------------------------------------------------------------------------


class FrozenTelemetryPrimitiveTests(unittest.TestCase):
    """Guards the pure frozen-telemetry primitives: a frozen ADVANCE needs UT to
    strictly increase while the other four fields stay bit-identical; a frozen UT
    (paused game) or any field change is NOT an advance."""

    def _sig(self, **kw):
        return mlib.frozen_signature(snap(**kw))

    def test_advance_requires_ut_strictly_greater(self):
        a = self._sig(ut=1.0, **_FROZEN_FIELDS)
        b = self._sig(ut=2.0, **_FROZEN_FIELDS)
        self.assertTrue(mlib.advances_frozen(a, b))
        # Frozen UT (identical full signature, a paused sim) is NOT an advance.
        self.assertFalse(mlib.advances_frozen(a, a))
        # UT going backwards is not an advance either.
        self.assertFalse(mlib.advances_frozen(b, a))

    def test_advance_requires_other_fields_bit_identical(self):
        a = self._sig(ut=1.0, **_FROZEN_FIELDS)
        changed = dict(_FROZEN_FIELDS, apoapsis=14000.0001)
        b = mlib.frozen_signature(snap(ut=2.0, **changed))
        self.assertFalse(mlib.advances_frozen(a, b))

    def test_none_and_nan_never_advance(self):
        a = self._sig(ut=1.0, **_FROZEN_FIELDS)
        self.assertFalse(mlib.advances_frozen(None, a))
        self.assertFalse(mlib.advances_frozen(a, None))
        nan = self._sig(ut=float("nan"), **_FROZEN_FIELDS)
        self.assertFalse(mlib.advances_frozen(a, nan))
        self.assertFalse(mlib.advances_frozen(nan, a))


class VesselLostTerminalTests(unittest.TestCase):
    """Guards the runner-signaled vessel-lost terminal (design vessel-destroyed
    terminal): a ``vessel_lost`` snapshot terminates ANY phase as MISSION-ASSERT-FAIL
    with a loss_reason, and the machine is inert afterwards."""

    def test_vessel_lost_in_descent_terminates_assert_fail_and_is_idempotent(self):
        state = _b1_descent_state(B1_PARAMS)
        new, actions = mlib.b1_decide(state, snap(ut=10.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertEqual(new.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIsNotNone(new.loss_reason)
        self.assertIn("vessel-lost", new.loss_reason)
        self.assertEqual(actions, [])
        # Once done the machine is inert (no repeated actions, state unchanged).
        again, acts2 = mlib.b1_decide(new, snap(ut=11.0))
        self.assertIs(again, new)
        self.assertEqual(acts2, [])

    def test_vessel_lost_in_prelaunch_also_terminates(self):
        # Runner-signaled loss is PHASE-INDEPENDENT: it fires even in PRELAUNCH,
        # where the frozen-telemetry detector is deliberately gated off.
        state = mlib.b1_initial_state(B1_PARAMS)
        new, actions = mlib.b1_decide(state, snap(ut=0.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertEqual(new.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", new.loss_reason)
        self.assertEqual(actions, [])
        self.assertEqual(new.phase, mlib.B1_PRELAUNCH)  # never left prelaunch

    def test_vessel_lost_terminates_b2(self):
        state = mlib.b2_initial_state(B2_PARAMS)
        state, _ = mlib.b2_decide(state, snap(ut=0.0))  # -> MJ-ASCENT
        new, actions = mlib.b2_decide(state, snap(ut=1.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertEqual(new.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", new.loss_reason)
        self.assertEqual(actions, [])


class FrozenTelemetryTerminalTests(unittest.TestCase):
    """Guards the airborne frozen-telemetry terminal (design vessel-destroyed
    terminal): N consecutive frozen samples terminate as MISSION-ASSERT-FAIL, a
    field change or a frozen UT resets the run, and PRELAUNCH is exempt."""

    def _seed_sig(self, **kw):
        # A prior sample matching the frozen run's fields with a LOWER ut, so the
        # first frozen frame already registers as advance #1 (the Nth frozen frame
        # then trips the terminal at exactly the limit).
        return mlib.frozen_signature(snap(ut=0.0, **dict(_FROZEN_FIELDS, **kw)))

    def test_frozen_descent_terminates_at_exactly_the_limit(self):
        # The freeze sits ABOVE the chute-deploy altitude (5000 m > 2500 m) so
        # the chute never deploys mid-freeze: with the chute deployed the same
        # trip is now the DOWN success terminal (option A, B1DownTerminalTests);
        # this cell guards the chute-less ASSERT-FAIL path at the exact limit.
        limit = 4
        params = _b1_params_with_limit(limit)
        seed = self._seed_sig(altitude=5000.0)
        # N-1 frozen samples: counter climbs but still running.
        before, _ = drive_b1(_b1_descent_state(params, frozen_sig=seed),
                             _frozen_frames(limit - 1, altitude=5000.0))
        self.assertFalse(before.done)
        self.assertIsNone(before.loss_reason)
        self.assertEqual(before.frozen_count, limit - 1)
        # The Nth frozen sample trips the terminal.
        after, _ = drive_b1(_b1_descent_state(params, frozen_sig=seed),
                            _frozen_frames(limit, altitude=5000.0))
        self.assertTrue(after.done)
        self.assertEqual(after.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("frozen", after.loss_reason)
        self.assertIn(str(limit), after.loss_reason)

    def test_change_in_any_single_field_resets_the_counter(self):
        limit = 5
        params = _b1_params_with_limit(limit)
        for field_name in ("altitude", "vertical_speed", "apoapsis", "periapsis"):
            state = _b1_descent_state(params, frozen_sig=self._seed_sig())
            frames = _frozen_frames(limit - 1)  # count -> limit-1
            # A live sample changing ONE field (UT still advancing) breaks the run.
            changed = dict(_FROZEN_FIELDS)
            changed[field_name] = _FROZEN_FIELDS[field_name] + 1.0
            frames.append(snap(ut=float(limit), **changed))
            state, _ = drive_b1(state, frames)
            self.assertEqual(state.frozen_count, 0,
                             "changing %s must reset the counter" % field_name)
            self.assertFalse(state.done, "changing %s must not terminate" % field_name)

    def test_frozen_ut_does_not_increment_counter(self):
        # A paused game repeats the FULL signature (UT included). UT never advances,
        # so no frame is a frozen ADVANCE -> the counter stays 0, never terminal.
        limit = 4
        params = _b1_params_with_limit(limit)
        seed = self._seed_sig()
        paused = [snap(ut=0.0, **_FROZEN_FIELDS) for _ in range(2 * limit)]
        state, _ = drive_b1(_b1_descent_state(params, frozen_sig=seed), paused)
        self.assertEqual(state.frozen_count, 0)
        self.assertFalse(state.done)
        self.assertIsNone(state.loss_reason)

    def test_prelaunch_static_telemetry_never_trips(self):
        # PRELAUNCH pad telemetry is legitimately static; the detector is gated off
        # there. Prime the counter to limit-1 with a matching signature and feed a
        # frozen-advance-looking frame: it must NOT terminate -- it just enters ASCENT.
        limit = 2
        params = _b1_params_with_limit(limit)
        state = mlib.b1_initial_state(params)  # PRELAUNCH
        state = state.__class__(**{**state.__dict__,
                                   "frozen_sig": self._seed_sig(),
                                   "frozen_count": limit - 1})
        new, _ = mlib.b1_decide(state, snap(ut=1.0, **_FROZEN_FIELDS))
        self.assertEqual(new.phase, mlib.B1_ASCENT)
        self.assertIsNone(new.loss_reason)
        self.assertFalse(new.done)

    def test_frozen_mj_ascent_terminates_b2(self):
        # B2 sibling: the same frozen-telemetry terminal fires in MJ-ASCENT.
        limit = 4
        params = _b2_params_with_limit(limit)
        base = mlib.b2_initial_state(params)
        # Airborne-but-short-of-target orbit fields, so MJ-ASCENT does not transition
        # (apoapsis 50000 < target 75000 threshold) while the freeze accumulates.
        fields = dict(altitude=30000.0, vertical_speed=100.0, apoapsis=50000.0, periapsis=-100000.0)
        seed = mlib.frozen_signature(snap(ut=0.0, **fields))
        state = base.__class__(**{**base.__dict__, "phase": mlib.B2_MJ_ASCENT,
                                  "phase_entry_ut": 0.0, "frozen_sig": seed})
        frames = [snap(ut=1.0 + i, **fields) for i in range(limit)]
        state, _ = drive_b2(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("frozen", state.loss_reason)


class LossReasonVerdictTests(unittest.TestCase):
    """Guards resolve_flight_verdict's loss_reason branch (design vessel-destroyed
    terminal): a non-None loss_reason maps to MISSION-ASSERT-FAIL and is returned
    VERBATIM, BEFORE any assertion evaluation."""

    def test_loss_reason_returned_verbatim_before_assertions(self):
        state = mlib.b1_initial_state(B1_PARAMS)
        reason = "vessel-lost (custom terminal reason)"
        lost = state.__class__(**{**state.__dict__,
                                  "verdict": mlib.MISSION_ASSERT_FAIL,
                                  "loss_reason": reason, "done": True})
        # Assertions that WOULD all be met: proves the loss_reason path short-circuits
        # BEFORE assertion evaluation (a destroyed craft's residual telemetry, which
        # could spuriously satisfy the window, must never certify the flight OK).
        all_met = mlib.evaluate_b1_assertions(
            [snap(apoapsis=14000.0, situation="LANDED") for _ in range(4)], B1_PARAMS)
        self.assertTrue(mlib.all_assertions_met(all_met))
        verdict, out_reason = mlib.resolve_flight_verdict(lost, all_met)
        self.assertEqual(verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertEqual(out_reason, reason)  # verbatim, not "all telemetry assertions met"


class FrozenSampleLimitParamTests(unittest.TestCase):
    """Guards the frozenTelemetrySamples missionParam: absent -> default 10,
    present -> parsed int, for both B1 and B2."""

    _B1 = {
        "throttle": 1.0, "apoapsisWindowMeters": {"min": 6000, "max": 30000},
        "chuteDeployAltMeters": 2500, "landedSituations": ["LANDED", "SPLASHED"],
        "ascentTimeoutSeconds": 90, "coastTimeoutSeconds": 180, "descentTimeoutSeconds": 240,
    }
    _B2 = {
        "targetApoapsisMeters": 80000, "targetPeriapsisMeters": 80000,
        "apoErrorMeters": 5000, "periErrorMeters": 5000, "eccentricityMax": 0.02,
        "inclinationErrorDeg": 2.0, "ascentTimeoutSeconds": 420, "circularizeTimeoutSeconds": 300,
    }

    def test_b1_default_and_parsed(self):
        self.assertEqual(mlib.b1_params_from_dict(self._B1).frozen_sample_limit, 10)
        p = mlib.b1_params_from_dict({**self._B1, "frozenTelemetrySamples": 25})
        self.assertEqual(p.frozen_sample_limit, 25)
        self.assertIsInstance(p.frozen_sample_limit, int)

    def test_b2_default_and_parsed(self):
        self.assertEqual(mlib.b2_params_from_dict(self._B2).frozen_sample_limit, 10)
        q = mlib.b2_params_from_dict({**self._B2, "frozenTelemetrySamples": 7})
        self.assertEqual(q.frozen_sample_limit, 7)
        self.assertIsInstance(q.frozen_sample_limit, int)


# ---------------------------------------------------------------------------
# B1 DOWN success terminal (operator decision 2026-07-20, option A): a
# chute-deployed impact at touchdown is a SUCCESSFUL end; B4 owns survival.
# ---------------------------------------------------------------------------


class B1DownTerminalTests(unittest.TestCase):
    """Guards the DOWN terminal: vessel-lost / frozen in DESCENT with the chute
    deployed ends DOWN (done, NO loss_reason, verdict None so assertions decide,
    settle tail skipped); without the chute -- and in every other phase -- the
    loss stays the ASSERT-FAIL loss_reason terminal. A regression here either
    fails every live B1 run (the Jumping Flea always breaks apart at touchdown)
    or silently passes a mid-air destruction."""

    def test_vessel_lost_in_descent_with_chute_is_down(self):
        state = _b1_descent_state(B1_PARAMS, chute_deployed=True)
        # Establish the "reached the ground" leg: last finite altitude below
        # downMaxAltMeters (default 500) before the loss frame.
        state, _ = mlib.b1_decide(state, snap(ut=9.0, altitude=120.0))
        new, actions = mlib.b1_decide(state, snap(ut=10.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertEqual(new.phase, mlib.B1_DOWN)
        self.assertIsNone(new.verdict)          # assertions decide, not the loss path
        self.assertIsNone(new.loss_reason)
        self.assertTrue(new.skip_settle_tail)   # vessel gone: no settle tail
        self.assertEqual(new.phases_reached[-1], mlib.B1_DOWN)
        self.assertEqual(actions, [])
        # Idempotent once done.
        again, acts2 = mlib.b1_decide(new, snap(ut=11.0))
        self.assertIs(again, new)
        self.assertEqual(acts2, [])

    def test_vessel_lost_in_descent_without_chute_stays_assert_fail(self):
        # No chute deployed: the craft was destroyed mid-air, not a DOWN success.
        state = _b1_descent_state(B1_PARAMS)  # chute_deployed defaults False
        new, _ = mlib.b1_decide(state, snap(ut=10.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertEqual(new.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", new.loss_reason)
        self.assertNotEqual(new.phase, mlib.B1_DOWN)
        self.assertFalse(new.skip_settle_tail)

    def test_frozen_limit_in_descent_with_chute_is_down(self):
        # The frozen-telemetry variant of the same signal: N frozen samples with
        # the chute deployed end DOWN, not ASSERT-FAIL. The freeze altitude sits
        # BELOW downMaxAltMeters (a freeze at altitude is the chute-ripped case
        # and stays ASSERT-FAIL - see the at-altitude sibling test).
        limit = 4
        params = _b1_params_with_limit(limit)
        low = dict(_FROZEN_FIELDS, altitude=150.0)
        seed = mlib.frozen_signature(snap(ut=0.0, **low))
        state = _b1_descent_state(params, chute_deployed=True, frozen_sig=seed)
        state, _ = drive_b1(state, _frozen_frames(limit, altitude=150.0))
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B1_DOWN)
        self.assertIsNone(state.verdict)
        self.assertIsNone(state.loss_reason)
        self.assertTrue(state.skip_settle_tail)

    def test_post_chute_loss_at_altitude_stays_assert_fail(self):
        # Fable review of PR #1335, SF-1 (the masking scenario): chute deployed
        # at 2500m, craft destroyed at ~1800m (chute ripped / mid-air breakup).
        # "Reached the ground" is NOT satisfied, so DOWN must NOT be awarded.
        state = _b1_descent_state(B1_PARAMS, chute_deployed=True)
        state, _ = mlib.b1_decide(state, snap(ut=9.0, altitude=1800.0))
        new, _ = mlib.b1_decide(state, snap(ut=10.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertNotEqual(new.phase, mlib.B1_DOWN)
        self.assertEqual(new.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("last altitude 1800m", new.loss_reason)

    def test_frozen_limit_in_descent_without_chute_stays_assert_fail(self):
        # The telemetry freezes ABOVE the chute-deploy altitude (5000 m > 2500 m),
        # so the chute never deploys during the freeze run: a chute-less frozen
        # DESCENT is a mid-air destruction, not a DOWN success.
        limit = 4
        params = _b1_params_with_limit(limit)
        seed = mlib.frozen_signature(snap(ut=0.0, **dict(_FROZEN_FIELDS, altitude=5000.0)))
        state = _b1_descent_state(params, frozen_sig=seed)
        state, _ = drive_b1(state, _frozen_frames(limit, altitude=5000.0))
        self.assertTrue(state.done)
        self.assertFalse(state.chute_deployed)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("frozen", state.loss_reason)
        self.assertNotEqual(state.phase, mlib.B1_DOWN)

    def test_earlier_phase_loss_still_fails_even_with_chute_flag(self):
        # DOWN is DESCENT-scoped: a loss in COAST fails even if the chute flag
        # were somehow set (a premature deploy does not make an ascent loss OK).
        base = mlib.b1_initial_state(B1_PARAMS)
        state = base.__class__(**{**base.__dict__, "phase": mlib.B1_COAST,
                                  "phase_entry_ut": 0.0, "chute_deployed": True})
        new, _ = mlib.b1_decide(state, snap(ut=5.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertEqual(new.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", new.loss_reason)
        self.assertNotEqual(new.phase, mlib.B1_DOWN)

    def test_down_state_with_met_assertions_resolves_mission_ok(self):
        # End-to-end verdict: DOWN (no loss_reason, verdict None) + met
        # assertions -> MISSION-OK.
        state = _b1_descent_state(B1_PARAMS, chute_deployed=True)
        state, _ = mlib.b1_decide(state, snap(ut=9.0, altitude=80.0))
        state, _ = mlib.b1_decide(state, snap(ut=10.0, vessel_lost=True))
        outs = mlib.evaluate_b1_assertions(
            [snap(apoapsis=14000.0) for _ in range(4)], B1_PARAMS, down_terminal=True)
        verdict, reason = mlib.resolve_flight_verdict(state, outs)
        self.assertEqual(verdict, mlib.MISSION_OK, reason)

    def test_landed_situation_assertion_accepts_down(self):
        # The final frames of a DOWN flight carry no landed situation (the craft
        # is debris); down_terminal=True satisfies landedSituation and NAMES the
        # end in the value so the result JSON says which end it was.
        frames = [snap(apoapsis=14000.0, situation="FLYING"),
                  snap(ut=1.0, vessel_lost=True)]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS, down_terminal=True)
        sit = outs[1]
        self.assertEqual(sit.name, "landedSituation")
        self.assertTrue(sit.met)
        self.assertEqual(sit.value, "DOWN(chute-deployed impact)")
        self.assertTrue(sit.detail["downTerminal"])
        # Without the flag the same frames stay UNMET (the plain path unchanged).
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertFalse(outs[1].met)
        self.assertFalse(outs[1].detail["downTerminal"])


# ---------------------------------------------------------------------------
# B4 reentry+splashdown machine, params, and assertions.
# ---------------------------------------------------------------------------


B4_PARAMS = mlib.B4Params(
    target_apoapsis=80000.0,
    target_periapsis=80000.0,
    apo_error=5000.0,
    peri_error=5000.0,
    ascent_timeout=420.0,
    circularize_timeout=300.0,
    deorbit_periapsis=25000.0,
    retro_settle_seconds=10.0,
    warp_above_alt=45000.0,
    warp_hop_seconds=120.0,
    chute_deploy_alt=3000.0,
    deorbit_timeout=300.0,
    reentry_timeout=3600.0,
    descent_timeout=600.0,
    landed_situations=("LANDED", "SPLASHED"),
)


def drive_b4(state, frames):
    per_frame = []
    for f in frames:
        state, actions = mlib.b4_decide(state, f)
        per_frame.append(actions)
    return state, per_frame


def _b4_state(phase, **overrides):
    """A B4State pinned in ``phase`` with phase_entry_ut=0."""
    base = mlib.b4_initial_state(B4_PARAMS)
    fields = {**base.__dict__, "phase": phase, "phase_entry_ut": 0.0}
    fields.update(overrides)
    return base.__class__(**fields)


class B4MachineTests(unittest.TestCase):
    """Guards the B4 machine: the ascent half must behave exactly like B2 (latch
    AND window, staged launch, guarded circularize), ORBIT must NOT be terminal,
    the deorbit burn must wait out the attitude settle and stop at the periapsis
    target, warp hops must fire only above the altitude threshold while
    descending, and the splashdown situation is the only success terminal."""

    def test_full_happy_path_walk(self):
        state = mlib.b4_initial_state(B4_PARAMS)
        frames = [
            snap(ut=0.0),                                               # 0 PRELAUNCH->MJ-ASCENT
            snap(ut=60.0, apoapsis=40000.0),                            # 1 climbing
            snap(ut=180.0, apoapsis=78000.0, mj_ascent_complete=True),  # 2 ->CIRCULARIZE
            snap(ut=200.0, apoapsis=80000.0, periapsis=20000.0),        # 3 pe rising
            snap(ut=260.0, apoapsis=80000.0, periapsis=79000.0),        # 4 ->ORBIT (not done!)
            snap(ut=261.0, apoapsis=80000.0, periapsis=79000.0,
                 altitude=80000.0),                                     # 5 ORBIT->DEORBIT (retro AP)
            snap(ut=265.0, apoapsis=80001.0, periapsis=79000.0,
                 altitude=80001.0),                                     # 6 settle wait (4s < 10s)
            snap(ut=272.0, apoapsis=80002.0, periapsis=79000.5,
                 altitude=80002.0, ap_error=1.5),                       # 7 settled + aligned -> throttle
            snap(ut=280.0, apoapsis=80002.0, periapsis=60000.0,
                 altitude=80000.0),                                     # 8 burning
            snap(ut=300.0, apoapsis=80002.0, periapsis=24000.0,
                 altitude=79000.0),                                     # 9 ->REENTRY (cut+release+stage)
            snap(ut=310.0, altitude=70000.0, vertical_speed=50.0,
                 periapsis=24000.0),                                    # 10 ascending: NO warp
            snap(ut=320.0, altitude=71000.0, vertical_speed=-10.0,
                 periapsis=24000.0),                                    # 11 descending high -> hop
            snap(ut=440.0, altitude=75000.0, vertical_speed=-200.0,
                 periapsis=24000.0),                                    # 12 still exo -> hop (70km floor, SF-3)
            snap(ut=560.0, altitude=40000.0, vertical_speed=-300.0,
                 periapsis=24000.0),                                    # 13 below threshold: no warp
            snap(ut=600.0, altitude=2500.0, vertical_speed=-200.0),     # 14 ->SPLASHDOWN + chute
            snap(ut=650.0, altitude=1000.0, vertical_speed=-8.0,
                 situation="FLYING"),                                   # 15 descending on chute
            snap(ut=700.0, altitude=0.0, situation="SPLASHED"),         # 16 terminal
        ]
        state, per_frame = drive_b4(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B4_SPLASHDOWN)
        self.assertIsNone(state.verdict)
        self.assertIsNone(state.loss_reason)
        self.assertTrue(state.chute_deployed)
        self.assertEqual(state.phases_reached,
                         (mlib.B4_PRELAUNCH, mlib.B4_MJ_ASCENT, mlib.B4_CIRCULARIZE,
                          mlib.B4_ORBIT, mlib.B4_DEORBIT, mlib.B4_REENTRY,
                          mlib.B4_SPLASHDOWN))
        # Frame-by-frame actions: launch, circularize, retro AP, settle silence,
        # throttle, burn-end triple, warp hops only on 11/12, chute on 14.
        self.assertEqual(per_frame[0], [
            Action(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, 80000.0),
            Action(mlib.ACTION_MJ_ENABLE_AUTOSTAGE),
            Action(mlib.ACTION_MJ_ENGAGE_ASCENT),
            Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(per_frame[2], [Action(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION)])
        self.assertEqual(per_frame[4], [])
        self.assertEqual(per_frame[5], [Action(mlib.ACTION_AP_POINT_RETROGRADE)])
        self.assertEqual(per_frame[6], [])   # attitude still settling: no throttle
        self.assertEqual(per_frame[7], [Action(mlib.ACTION_SET_THROTTLE, 1.0)])
        self.assertEqual(per_frame[8], [])   # burn latch: throttle issued ONCE
        self.assertEqual(per_frame[9], [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                        Action(mlib.ACTION_AP_DISENGAGE),
                                        Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(per_frame[10], [])  # ascending: warp gated off
        self.assertEqual(per_frame[11], [Action(mlib.ACTION_WARP_TO, 320.0 + 120.0)])
        self.assertEqual(per_frame[12], [Action(mlib.ACTION_WARP_TO, 440.0 + 120.0)])
        self.assertEqual(per_frame[13], [])  # below warpAboveAltMeters: no hop
        self.assertEqual(per_frame[14], [Action(mlib.ACTION_DEPLOY_CHUTE)])
        self.assertEqual(per_frame[16], [])

    def test_orbit_is_not_terminal(self):
        # Regression vs B2 (where ORBIT sets done): B4 must keep flying.
        state = _b4_state(mlib.B4_CIRCULARIZE)
        state, _ = mlib.b4_decide(state, snap(ut=10.0, periapsis=79000.0))
        self.assertEqual(state.phase, mlib.B4_ORBIT)
        self.assertFalse(state.done)

    def test_mj_ascent_needs_latch_and_window(self):
        # VERBATIM the B2 gate: window alone (no latch) must not advance.
        state = _b4_state(mlib.B4_MJ_ASCENT)
        state, actions = mlib.b4_decide(state, snap(ut=60.0, apoapsis=78000.0))
        self.assertEqual(state.phase, mlib.B4_MJ_ASCENT)
        self.assertEqual(actions, [])
        # Latch alone (apoapsis short) must not advance either.
        state, actions = mlib.b4_decide(
            state, snap(ut=61.0, apoapsis=60000.0, mj_ascent_complete=True))
        self.assertEqual(state.phase, mlib.B4_MJ_ASCENT)
        self.assertEqual(actions, [])

    def test_deorbit_burn_waits_out_settle_then_throttles_once(self):
        state = _b4_state(mlib.B4_DEORBIT)
        # Before the 10 s settle: no throttle even when aligned (the snapshot
        # ap_error default is NaN = not aligned per SF-2, so aligned frames set
        # it explicitly).
        state, actions = mlib.b4_decide(state, snap(ut=4.0, periapsis=79000.0, ap_error=0.0))
        self.assertEqual(actions, [])
        self.assertFalse(state.burn_started)
        # Past the settle AND aligned: exactly one throttle-up.
        state, actions = mlib.b4_decide(state, snap(ut=11.0, periapsis=79000.0, ap_error=0.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 1.0)])
        self.assertTrue(state.burn_started)
        state, actions = mlib.b4_decide(state, snap(ut=12.0, periapsis=70000.0, ap_error=0.0))
        self.assertEqual(actions, [])

    def test_deorbit_burn_gated_on_attitude_error(self):
        # First live B4 flight (2026-07-20): a time-only settle burned mid-flip
        # (ship pointing radial, apoapsis 84km -> 382km). The burn must wait
        # for BOTH the settle time AND AutoPilot alignment.
        state = _b4_state(mlib.B4_DEORBIT)
        # Settled but still flipping (90 deg off): no throttle.
        state, actions = mlib.b4_decide(
            state, snap(ut=20.0, periapsis=79000.0, ap_error=90.0))
        self.assertEqual(actions, [])
        self.assertFalse(state.burn_started)
        # Still misaligned above the 5 deg default: no throttle.
        state, actions = mlib.b4_decide(
            state, snap(ut=30.0, periapsis=79000.0, ap_error=5.1))
        self.assertEqual(actions, [])
        self.assertFalse(state.burn_started)
        # Aligned: throttle up.
        state, actions = mlib.b4_decide(
            state, snap(ut=40.0, periapsis=79000.0, ap_error=3.2))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 1.0)])
        self.assertTrue(state.burn_started)

    def test_deorbit_nan_attitude_error_never_burns(self):
        # NaN = AP unreadable/not engaged: the gate fails closed and the phase
        # ends as the bounded deorbit-budget flake, never a blind burn.
        state = _b4_state(mlib.B4_DEORBIT)
        state, actions = mlib.b4_decide(
            state, snap(ut=50.0, periapsis=79000.0, ap_error=float("nan")))
        self.assertEqual(actions, [])
        self.assertFalse(state.burn_started)
        # Budget expiry (deorbit_timeout default 300) -> MISSION-FLAKE.
        state, actions = mlib.b4_decide(
            state, snap(ut=400.0, periapsis=79000.0, ap_error=float("nan")))
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertTrue(state.done)

    def test_vessel_lost_in_deorbit_is_assert_fail(self):
        state = _b4_state(mlib.B4_DEORBIT, burn_started=True)
        new, actions = mlib.b4_decide(state, snap(ut=20.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertEqual(new.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", new.loss_reason)
        self.assertEqual(actions, [])

    def test_vessel_lost_in_splashdown_with_chute_is_still_assert_fail(self):
        # B4 has NO DOWN equivalent: even chute-deployed, a lost vessel in the
        # descent is a failed survival contract (the reentry burned it up or the
        # splashdown destroyed it).
        state = _b4_state(mlib.B4_SPLASHDOWN, chute_deployed=True)
        new, _ = mlib.b4_decide(state, snap(ut=20.0, vessel_lost=True))
        self.assertTrue(new.done)
        self.assertEqual(new.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", new.loss_reason)
        self.assertFalse(getattr(new, "skip_settle_tail", False))

    def test_frozen_telemetry_in_reentry_is_assert_fail(self):
        limit = 4
        params = B4_PARAMS.__class__(**{**B4_PARAMS.__dict__, "frozen_sample_limit": limit})
        fields = dict(altitude=60000.0, vertical_speed=-200.0,
                      apoapsis=80000.0, periapsis=24000.0)
        seed = mlib.frozen_signature(snap(ut=0.0, **fields))
        base = mlib.b4_initial_state(params)
        state = base.__class__(**{**base.__dict__, "phase": mlib.B4_REENTRY,
                                  "phase_entry_ut": 0.0, "frozen_sig": seed})
        frames = [snap(ut=1.0 + i, **fields) for i in range(limit)]
        state, _ = drive_b4(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("frozen", state.loss_reason)

    def test_budget_flakes_name_their_phase(self):
        # Every timed phase must flake past its budget, naming itself.
        cells = [
            (mlib.B4_MJ_ASCENT, snap(ut=500.0, apoapsis=40000.0)),          # > 420
            (mlib.B4_CIRCULARIZE, snap(ut=400.0, periapsis=20000.0)),       # > 300
            (mlib.B4_DEORBIT, snap(ut=400.0, periapsis=79000.0)),           # > 300
            (mlib.B4_REENTRY, snap(ut=4000.0, altitude=60000.0,
                                   vertical_speed=-10.0)),                  # > 3600
            (mlib.B4_SPLASHDOWN, snap(ut=700.0, altitude=500.0,
                                      situation="FLYING")),                 # > 600
        ]
        for phase, frame in cells:
            with self.subTest(phase=phase):
                overrides = {"burn_started": True} if phase == mlib.B4_DEORBIT else {}
                state = _b4_state(phase, **overrides)
                state, actions = mlib.b4_decide(state, frame)
                self.assertTrue(state.done)
                self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
                self.assertEqual(state.flake_phase, phase)
                self.assertEqual(actions, [], "a flaked frame must emit no actions")

    def test_no_warp_hop_on_flaked_reentry_frame(self):
        # The frame that flakes REENTRY must not ALSO command a warp hop.
        state = _b4_state(mlib.B4_REENTRY)
        state, actions = mlib.b4_decide(
            state, snap(ut=4000.0, altitude=60000.0, vertical_speed=-100.0))
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(actions, [])

    def test_done_state_is_idempotent(self):
        state = _b4_state(mlib.B4_SPLASHDOWN, done=True)
        new, actions = mlib.b4_decide(state, snap(ut=999.0))
        self.assertIs(new, state)
        self.assertEqual(actions, [])


class B4ParamTests(unittest.TestCase):
    """Guards b4_params_from_dict: spec keys parse into the typed params, and the
    deorbit-side defaults hold when the optional keys are absent."""

    _REQUIRED = {
        "targetApoapsisMeters": 80000, "targetPeriapsisMeters": 80000,
        "apoErrorMeters": 5000, "periErrorMeters": 5000,
        "ascentTimeoutSeconds": 420, "circularizeTimeoutSeconds": 300,
        "deorbitPeriapsisMeters": 25000, "retroSettleSeconds": 10,
        "warpAboveAltMeters": 45000, "warpHopSeconds": 120,
        "chuteDeployAltMeters": 3000, "deorbitTimeoutSeconds": 300,
        "reentryTimeoutSeconds": 3600, "descentTimeoutSeconds": 600,
        "landedSituations": ["LANDED", "SPLASHED"],
    }

    def test_full_spec_dict_round_trip(self):
        p = mlib.b4_params_from_dict(dict(self._REQUIRED))
        self.assertEqual(p.target_apoapsis, 80000.0)
        self.assertEqual(p.deorbit_periapsis, 25000.0)
        self.assertEqual(p.retro_settle_seconds, 10.0)
        self.assertEqual(p.warp_above_alt, 45000.0)
        self.assertEqual(p.warp_hop_seconds, 120.0)
        self.assertEqual(p.chute_deploy_alt, 3000.0)
        self.assertEqual(p.landed_situations, ("LANDED", "SPLASHED"))
        self.assertEqual(p.frozen_sample_limit, 10)  # default when absent

    def test_defaults_when_keys_absent(self):
        p = mlib.b4_params_from_dict({})
        self.assertEqual(p.deorbit_periapsis, 25000.0)
        self.assertEqual(p.retro_settle_seconds, 10.0)
        self.assertEqual(p.warp_above_alt, 70000.0)
  # exo-only hops (SF-3)
        self.assertEqual(p.warp_hop_seconds, 120.0)
        self.assertEqual(p.chute_deploy_alt, 3000.0)
        self.assertEqual(p.deorbit_timeout, 300.0)
        self.assertEqual(p.reentry_timeout, 3600.0)
        self.assertEqual(p.descent_timeout, 600.0)
        self.assertEqual(p.frozen_sample_limit, 10)

    def test_frozen_limit_parsed(self):
        p = mlib.b4_params_from_dict({**self._REQUIRED, "frozenTelemetrySamples": 7})
        self.assertEqual(p.frozen_sample_limit, 7)
        self.assertIsInstance(p.frozen_sample_limit, int)


class B4AssertionTests(unittest.TestCase):
    """Guards evaluate_b4_assertions: terminal-focused, derivable from frames +
    phase evidence, no orbital precision post-deorbit."""

    def _phases(self, through_orbit=True):
        if through_orbit:
            return (mlib.B4_PRELAUNCH, mlib.B4_MJ_ASCENT, mlib.B4_CIRCULARIZE,
                    mlib.B4_ORBIT, mlib.B4_DEORBIT, mlib.B4_REENTRY, mlib.B4_SPLASHDOWN)
        return (mlib.B4_PRELAUNCH, mlib.B4_MJ_ASCENT)

    def _frames(self, peak=80000.0, final="SPLASHED"):
        return [snap(apoapsis=peak, situation="FLYING"),
                snap(apoapsis=peak - 100.0, situation=final)]

    def test_all_met(self):
        outs = mlib.evaluate_b4_assertions(self._frames(), B4_PARAMS,
                                           phases_reached=self._phases(),
                                           chute_deployed=True)
        self.assertEqual([o.name for o in outs],
                         ["reachedOrbit", "apoapsisFloor", "landedSituation",
                          "chuteDeployed"])
        self.assertTrue(mlib.all_assertions_met(outs))
        self.assertEqual(outs[0].value, mlib.B4_SPLASHDOWN)  # deepest phase
        self.assertEqual(outs[1].value, 80000.0)             # the peak
        self.assertEqual(outs[2].value, "SPLASHED")

    def test_orbit_never_reached_unmet(self):
        outs = mlib.evaluate_b4_assertions(self._frames(), B4_PARAMS,
                                           phases_reached=self._phases(False),
                                           chute_deployed=True)
        self.assertFalse(outs[0].met)
        self.assertEqual(outs[0].value, mlib.B4_MJ_ASCENT)

    def test_apoapsis_floor_boundary_inclusive(self):
        # floor = 80000 - 5000 = 75000: exactly 75000 met, just below unmet.
        outs = mlib.evaluate_b4_assertions(self._frames(peak=75000.0), B4_PARAMS,
                                           phases_reached=self._phases(),
                                           chute_deployed=True)
        self.assertTrue(outs[1].met)
        outs = mlib.evaluate_b4_assertions(self._frames(peak=74999.0), B4_PARAMS,
                                           phases_reached=self._phases(),
                                           chute_deployed=True)
        self.assertFalse(outs[1].met)

    def test_all_nan_apoapsis_unmet_value_null(self):
        frames = [snap(apoapsis=float("nan"), situation="SPLASHED") for _ in range(3)]
        outs = mlib.evaluate_b4_assertions(frames, B4_PARAMS,
                                           phases_reached=self._phases(),
                                           chute_deployed=True)
        self.assertFalse(outs[1].met)
        self.assertIsNone(outs[1].to_dict()["value"])

    def test_final_situation_and_chute(self):
        outs = mlib.evaluate_b4_assertions(self._frames(final="FLYING"), B4_PARAMS,
                                           phases_reached=self._phases(),
                                           chute_deployed=False)
        self.assertFalse(outs[2].met)   # never landed
        self.assertFalse(outs[3].met)   # chute never deployed
        self.assertIs(outs[3].value, False)

    def test_loss_reason_short_circuits_before_assertions(self):
        # A B4 vessel-lost terminal must fail even with all-met assertions (the
        # survival contract; mirrors the B1 LossReasonVerdictTests cell).
        state = _b4_state(mlib.B4_REENTRY)
        state, _ = mlib.b4_decide(state, snap(ut=20.0, vessel_lost=True))
        outs = mlib.evaluate_b4_assertions(self._frames(), B4_PARAMS,
                                           phases_reached=self._phases(),
                                           chute_deployed=True)
        self.assertTrue(mlib.all_assertions_met(outs))
        verdict, reason = mlib.resolve_flight_verdict(state, outs)
        self.assertEqual(verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", reason)


B5_PARAMS = mlib.B5Params(
    target_apoapsis=80000.0,
    target_periapsis=80000.0,
    apo_error=5000.0,
    peri_error=5000.0,
    ascent_timeout=420.0,
    circularize_timeout=300.0,
    target_body="Mun",
    home_body="Kerbin",
    transfer_min_apoapsis=10_000_000.0,
    course_correct_periapsis=60000.0,
    plan_timeout=300.0,
    plan_retry_seconds=30.0,
    transfer_burn_timeout=4000.0,
    coast_timeout=400_000.0,
    flyby_timeout=300_000.0,
    coast_warp_factor=6,
    flyby_warp_factor=5,
    target_periapsis_floor=10000.0,
)


def drive_b5(state, frames):
    per_frame = []
    for f in frames:
        state, actions = mlib.b5_decide(state, f)
        per_frame.append(actions)
    return state, per_frame


def _b5_state(phase, **overrides):
    """A B5State pinned in ``phase`` with phase_entry_ut=0."""
    base = mlib.b5_initial_state(B5_PARAMS)
    fields = {**base.__dict__, "phase": phase, "phase_entry_ut": 0.0}
    fields.update(overrides)
    return base.__class__(**fields)


class B5MachineTests(unittest.TestCase):
    """Guards the B5 machine: the ascent half must behave exactly like B4/B2,
    the plan phases must hand a node to the executor exactly once and re-plan
    only while no node exists, the TLI-burn exit needs BOTH an empty node list
    AND the apoapsis floor, the coast/flyby SOI gates dispatch on body name
    (with "" staying in phase), and RETURN entry is the only success terminal."""

    def test_full_happy_path_walk(self):
        state = mlib.b5_initial_state(B5_PARAMS)
        frames = [
            snap(ut=0.0, body="Kerbin"),                                # 0 PRELAUNCH->MJ-ASCENT
            snap(ut=180.0, apoapsis=78000.0, mj_ascent_complete=True,
                 body="Kerbin"),                                        # 1 ->CIRCULARIZE
            snap(ut=260.0, apoapsis=80000.0, periapsis=79000.0,
                 body="Kerbin"),                                        # 2 ->ORBIT
            snap(ut=261.0, apoapsis=80000.0, periapsis=79000.0,
                 body="Kerbin"),                                        # 3 ORBIT->PLAN-TRANSFER (target+plan)
            snap(ut=265.0, apoapsis=80000.0, periapsis=79000.0,
                 body="Kerbin", node_count=1),                          # 4 node -> TRANSFER-BURN (execute)
            snap(ut=300.0, apoapsis=80000.0, periapsis=79000.0,
                 body="Kerbin", node_count=1),                          # 5 executor warping/burning
            snap(ut=2200.0, apoapsis=11_500_000.0, periapsis=79000.0,
                 body="Kerbin", node_count=0,
                 altitude=90_000.0),                                    # 6 burn done -> COAST
            snap(ut=2210.0, apoapsis=11_500_000.0, body="Kerbin",
                 altitude=95_000.0),                                    # 7 trigger 0 -> PLAN-CORRECTION (round 1)
            snap(ut=2230.0, apoapsis=11_500_000.0, body="Kerbin",
                 node_count=1),                                         # 8 node -> CORRECTION-BURN (AP point)
            snap(ut=2235.0, apoapsis=11_500_000.0, body="Kerbin",
                 node_count=1, node_dv=110.0, ap_error=1.4),            # 9 settling (streak 1) -> flip phys warp
            snap(ut=2245.0, apoapsis=11_500_000.0, body="Kerbin",
                 node_count=1, node_dv=110.0, ap_error=1.2,
                 warp_mode="PHYSICS", warp_rate=2.0),                   # 10 settled + streak 2 -> drop phys warp
            snap(ut=2247.0, apoapsis=11_500_000.0, body="Kerbin",
                 node_count=1, node_dv=110.0, ap_error=1.1),            # 11 warp NONE -> throttle
            snap(ut=2260.0, apoapsis=11_400_000.0, body="Kerbin",
                 node_count=1, node_dv=40.0, ap_error=1.0),             # 12 burning
            snap(ut=2270.0, apoapsis=11_350_000.0, body="Kerbin",
                 node_count=1, node_dv=1.5, ap_error=1.0),              # 13 dv <= cut -> cut triple
            snap(ut=2400.0, apoapsis=11_350_000.0, body="Kerbin",
                 altitude=200_000.0),                                   # 14 coast: stair capped by ALTITUDE limits
            snap(ut=8000.0, apoapsis=11_350_000.0, body="Kerbin",
                 altitude=6_500_000.0),                                 # 15 trigger 6M -> PLAN-CORRECTION (round 2)
            snap(ut=8010.0, apoapsis=11_350_000.0, body="Kerbin",
                 altitude=6_510_000.0, node_count=1),                   # 16 node -> CORRECTION-BURN (AP point)
            snap(ut=8018.0, apoapsis=11_350_000.0, body="Kerbin",
                 altitude=6_515_000.0, node_count=1, node_dv=4.0,
                 ap_error=0.9),                                         # 17 settling (streak 1) -> flip phys warp
            snap(ut=8025.0, apoapsis=11_350_000.0, body="Kerbin",
                 altitude=6_520_000.0, node_count=1, node_dv=4.0,
                 ap_error=0.8, warp_mode="PHYSICS", warp_rate=2.0),     # 18 settled + streak 2 -> drop phys warp
            snap(ut=8030.0, apoapsis=11_350_000.0, body="Kerbin",
                 altitude=6_525_000.0, node_count=0),                   # 19 node consumed -> cut pair
            snap(ut=9000.0, apoapsis=11_350_000.0, body="Kerbin",
                 altitude=7_000_000.0),                                 # 20 coast (rounds spent) -> full warp
            snap(ut=40_000.0, altitude=2_000_000.0, body="Mun"),        # 21 SOI! -> TARGET-FLYBY
            snap(ut=40_100.0, altitude=800_000.0, body="Mun",
                 periapsis=61_000.0),                                   # 22 outer leg -> flyby stair above floor
            snap(ut=40_700.0, altitude=61_000.0, body="Mun",
                 periapsis=61_000.0, warp_mode="RAILS",
                 warp_rate=1000.0),                                     # 23 periapsis area -> flyby floor
            snap(ut=80_000.0, altitude=3_000_000.0, body="Kerbin"),     # 24 home SOI -> RETURN terminal
        ]
        state, per_frame = drive_b5(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B5_RETURN)
        self.assertIsNone(state.verdict)
        self.assertIsNone(state.loss_reason)
        self.assertEqual(state.min_target_altitude, 61_000.0)
        self.assertEqual(state.correction_rounds_done, 2)
        self.assertEqual(state.phases_reached,
                         (mlib.B5_PRELAUNCH, mlib.B5_MJ_ASCENT, mlib.B5_CIRCULARIZE,
                          mlib.B5_ORBIT, mlib.B5_PLAN_TRANSFER, mlib.B5_TRANSFER_BURN,
                          mlib.B5_COAST_TO_TARGET,
                          mlib.B5_PLAN_CORRECTION, mlib.B5_CORRECTION_BURN,
                          mlib.B5_COAST_TO_TARGET,
                          mlib.B5_PLAN_CORRECTION, mlib.B5_CORRECTION_BURN,
                          mlib.B5_COAST_TO_TARGET, mlib.B5_TARGET_FLYBY,
                          mlib.B5_RETURN))
        self.assertEqual(per_frame[0], [
            Action(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, 80000.0),
            Action(mlib.ACTION_MJ_ENABLE_AUTOSTAGE),
            Action(mlib.ACTION_MJ_ENGAGE_ASCENT),
            Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(per_frame[1], [Action(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION)])
        self.assertEqual(per_frame[3], [
            Action(mlib.ACTION_SET_TARGET_BODY, text="Mun"),
            Action(mlib.ACTION_MJ_PLAN_TRANSFER)])
        self.assertEqual(per_frame[4], [Action(mlib.ACTION_MJ_EXECUTE_NODES)])
        self.assertEqual(per_frame[5], [])   # executor owns the TLI: machine silent
        self.assertEqual(per_frame[6], [])   # TRANSFER-BURN -> COAST transition frame
        # Trigger prelude steps straight to the PLAN rails hold (10x --
        # operator PR gate: plan phases never idle at 1x).
        self.assertEqual(per_frame[7], [Action(mlib.ACTION_SET_RAILS_WARP, 2.0),
                                        Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT, 60000.0,
                                               limit=150.0)])
        self.assertEqual(per_frame[8], [Action(mlib.ACTION_AP_POINT_NODE)])
        # Flip under mild physics warp (streak 1, still settling), dropped on
        # the gate-open frame, throttle only on the following warp-NONE frame.
        self.assertEqual(per_frame[9], [Action(mlib.ACTION_SET_PHYSICS_WARP, 1.0)])
        self.assertEqual(per_frame[10], [Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0)])
        self.assertEqual(per_frame[11], [Action(mlib.ACTION_SET_THROTTLE, 0.25)])
        self.assertEqual(per_frame[12], [])  # burn latch: throttle issued ONCE
        self.assertEqual(per_frame[13], [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                         Action(mlib.ACTION_AP_DISENGAGE),
                                         Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])
        # 200 km Kerbin: the trigger stair wants 6 (10,000x) but the stock
        # altitude table only legalizes 100x there -- command the ACHIEVABLE 4
        # (the old commanded-6 ran the whole leg silently clamped to 50x).
        self.assertEqual(per_frame[14], [Action(mlib.ACTION_SET_RAILS_WARP, 4.0)])
        self.assertEqual(per_frame[15], [Action(mlib.ACTION_SET_RAILS_WARP, 2.0),
                                         Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT, 60000.0,
                                                limit=150.0)])
        self.assertEqual(per_frame[16], [Action(mlib.ACTION_AP_POINT_NODE)])
        self.assertEqual(per_frame[17], [Action(mlib.ACTION_SET_PHYSICS_WARP, 1.0)])
        self.assertEqual(per_frame[18], [Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0)])
        self.assertEqual(per_frame[19], [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                         Action(mlib.ACTION_AP_DISENGAGE)])
        self.assertEqual(per_frame[20], [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])
        self.assertEqual(per_frame[21], [])  # SOI-entry transition frame
        # Outer flyby leg: the stair runs ABOVE the 100x floor (self-healing
        # re-emit: the game reads warp NONE despite the held command).
        self.assertEqual(per_frame[22], [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])
        # Periapsis area: back down to the flyby floor, altitude-legal at 61 km.
        self.assertEqual(per_frame[23], [Action(mlib.ACTION_SET_RAILS_WARP, 5.0)])
        self.assertEqual(per_frame[24], [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])  # RETURN: drop warp

    def test_plan_transfer_replans_only_while_no_node(self):
        # A failed plan (server-side OperationException -> no node) re-issues on
        # the bounded cadence; a node appearing hands off EXACTLY once.
        state = _b5_state(mlib.B5_PLAN_TRANSFER, last_plan_ut=0.0)
        # 10s after the last plan: below the 30s cadence, no re-plan.
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin"))
        self.assertEqual(actions, [])
        # 35s: cadence reached, re-plan (still no node).
        state, actions = mlib.b5_decide(state, snap(ut=35.0, body="Kerbin"))
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_PLAN_TRANSFER)])
        self.assertEqual(state.last_plan_ut, 35.0)
        # Node appeared: execute + transition; NEVER another plan (stacking guard).
        state, actions = mlib.b5_decide(state, snap(ut=40.0, body="Kerbin", node_count=1))
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_EXECUTE_NODES)])
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)

    def test_plan_transfer_timeout_flakes(self):
        # PLAN-TRANSFER expiry is a FLAKE (no transfer = no mission), unlike
        # PLAN-CORRECTION's fall-through.
        state = _b5_state(mlib.B5_PLAN_TRANSFER, last_plan_ut=0.0)
        state, _ = mlib.b5_decide(state, snap(ut=301.0, body="Kerbin"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.B5_PLAN_TRANSFER)

    def test_plan_correction_timeout_falls_through_to_coast(self):
        # The correction is best-effort: budget expiry proceeds to the coast
        # instead of flaking (MechJeb may transiently see no encounter), and
        # CONSUMES the round so a disqualified plan can never re-trigger
        # immediately (the next trigger altitude owns any further refinement).
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0)
        state, actions = mlib.b5_decide(state, snap(ut=301.0, body="Kerbin"))
        self.assertFalse(state.done)
        self.assertIsNone(state.verdict)
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)
        self.assertEqual(actions, [])

    def test_transfer_burn_needs_empty_nodes_and_apoapsis_floor(self):
        state = _b5_state(mlib.B5_TRANSFER_BURN)
        # Nodes empty but apoapsis still low (executor aborted without burning):
        # stay -- the budget owns the outcome.
        state, _ = mlib.b5_decide(state, snap(ut=10.0, apoapsis=90000.0,
                                              body="Kerbin", node_count=0))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        # Apoapsis high but a node still pending: stay (mid-burn).
        state, _ = mlib.b5_decide(state, snap(ut=20.0, apoapsis=11_000_000.0,
                                              body="Kerbin", node_count=1))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        # Both: advance into the coast (the correction rounds are
        # COAST-triggered now, never a direct TRANSFER-BURN branch).
        state, actions = mlib.b5_decide(state, snap(ut=30.0, apoapsis=11_000_000.0,
                                                    body="Kerbin", node_count=0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(actions, [])
        # The first COAST frame crosses trigger 0 -> PLAN-CORRECTION (round 1).
        state, actions = mlib.b5_decide(state, snap(ut=40.0, apoapsis=11_000_000.0,
                                                    altitude=90_000.0, body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        # The prelude steps straight to the PLAN rails hold (operator PR
        # gate: plan phases never idle at 1x).
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 2.0),
                                   Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT, 60000.0,
                                          limit=150.0)])

    def test_transfer_burn_capture_stray_node_cleared_on_exit(self):
        # First live B5 flight (2026-07-21, both attempts): OperationTransfer
        # planned a capture/arrival burn as a SECOND node; the executor consumed
        # the TLI node (2 -> 1) and autowarped toward the arrival node while the
        # old node_count==0 exit parked the machine until the burn budget
        # flaked. The exit is now "node_count fell below the planned handoff
        # count AND the apoapsis floor", with the stray aborted+cleared.
        state = _b5_state(mlib.B5_PLAN_TRANSFER, last_plan_ut=0.0)
        # Plan produced TWO nodes: handoff records planned_node_count=2.
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin", node_count=2))
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_EXECUTE_NODES)])
        self.assertEqual(state.planned_node_count, 2)
        # Mid-TLI: apoapsis crosses the floor but BOTH nodes still pending --
        # must NOT exit (the burn is still flying).
        state, actions = mlib.b5_decide(state, snap(ut=100.0, apoapsis=10_500_000.0,
                                                    body="Kerbin", node_count=2))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        self.assertEqual(actions, [])
        # TLI consumed (2 -> 1) + floor: exit into the coast, clearing the
        # stray arrival node on the way out.
        state, actions = mlib.b5_decide(state, snap(ut=200.0, apoapsis=11_300_000.0,
                                                    body="Kerbin", node_count=1))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])

    def test_correction_burn_diy_cut_on_threshold(self):
        # DIY burner happy flow: AP-point handoff, settle+align gate, ONE
        # throttle, cut when remaining dv reaches the threshold (round
        # consumed, full cleanup incl. clearing the sub-threshold node).
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin", node_count=1))
        self.assertEqual(actions, [Action(mlib.ACTION_AP_POINT_NODE)])
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        # Aligned but not settled (5 s < 10 s): no throttle; the flip engages
        # mild physics warp (on-change).
        state, actions = mlib.b5_decide(state, snap(ut=15.0, body="Kerbin",
                                                    node_count=1, node_dv=100.0,
                                                    ap_error=1.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 1.0)])
        # Settled but misaligned (|error| beyond the rough 30-degree start
        # gate; the sign is irrelevant -- live kRPC readings go negative): no
        # throttle; physics warp HELD (no re-emission while the game reads
        # PHYSICS).
        state, actions = mlib.b5_decide(state, snap(ut=21.0, body="Kerbin",
                                                    node_count=1, node_dv=100.0,
                                                    ap_error=-178.0,
                                                    warp_mode="PHYSICS",
                                                    warp_rate=2.0))
        self.assertEqual(actions, [])
        # First aligned frame after the misalign reset: streak 1 of 2, no fire.
        state, actions = mlib.b5_decide(state, snap(ut=23.0, body="Kerbin",
                                                    node_count=1, node_dv=100.0,
                                                    ap_error=1.0,
                                                    warp_mode="PHYSICS",
                                                    warp_rate=2.0))
        self.assertEqual(actions, [])
        # Settled AND aligned for the debounce depth: the gate opens, but the
        # burn must start at 1x -- the physics warp is dropped FIRST.
        state, actions = mlib.b5_decide(state, snap(ut=25.0, body="Kerbin",
                                                    node_count=1, node_dv=100.0,
                                                    ap_error=1.0,
                                                    warp_mode="PHYSICS",
                                                    warp_rate=2.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0)])
        self.assertFalse(state.corr_burn_started)
        # Next frame reads warp NONE: exactly one throttle-up.
        state, actions = mlib.b5_decide(state, snap(ut=26.0, body="Kerbin",
                                                    node_count=1, node_dv=100.0,
                                                    ap_error=1.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 0.25)])
        self.assertTrue(state.corr_burn_started)
        # Cut at/below the threshold.
        state, actions = mlib.b5_decide(state, snap(ut=40.0, body="Kerbin",
                                                    node_count=1, node_dv=1.8,
                                                    ap_error=1.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)
        self.assertEqual(actions, [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                   Action(mlib.ACTION_AP_DISENGAGE),
                                   Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])

    def test_transfer_burn_stagnation_watchdog_unwedges_executor(self):
        """Fifth live flight (2026-07-22): the executor BURNED a node then held
        it forever (orbit changed, then static at 1x, node pending) until the
        phase budget flaked. In TRANSFER-BURN the watchdog treats
        changed-since-entry + static-at-1x for burnStagnantSeconds as burn
        complete (with the apoapsis floor met): abort+clear the stale node and
        coast."""
        state = _b5_state(mlib.B5_PLAN_TRANSFER, last_plan_ut=0.0)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, apoapsis=84_000.0,
                                              periapsis=79_000.0, body="Kerbin",
                                              node_count=1))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        # The burn: apsides moving frame to frame (never static).
        state, _ = mlib.b5_decide(state, snap(ut=20.0, apoapsis=5_000_000.0,
                                              periapsis=76_000.0, body="Kerbin",
                                              node_count=1))
        # Post-burn wedge ABOVE the floor: orbit static at 1x, node pending.
        wedged = dict(apoapsis=11_322_716.568, periapsis=76_555.0,
                      body="Kerbin", node_count=1)
        state, _ = mlib.b5_decide(state, snap(ut=30.0, **wedged))
        state, _ = mlib.b5_decide(state, snap(ut=40.0, **wedged))   # static run starts
        state, actions = mlib.b5_decide(state, snap(ut=100.0, **wedged))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)        # < 120s: wait
        self.assertEqual(actions, [])
        state, actions = mlib.b5_decide(state, snap(ut=161.0, **wedged))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)      # unwedge + coast
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])

    def test_transfer_burn_autowarp_never_counts_as_stagnant(self):
        """RAILS autowarp toward the node (static orbit, warp != NONE) never
        counts toward the TRANSFER-BURN watchdog, no matter how long."""
        state = _b5_state(mlib.B5_PLAN_TRANSFER, last_plan_ut=0.0)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, apoapsis=84_000.0,
                                              periapsis=79_000.0, body="Kerbin",
                                              node_count=1))
        warped = dict(apoapsis=84_000.0, periapsis=79_000.0, body="Kerbin",
                      node_count=1, warp_mode="RAILS", warp_rate=100.0)
        for ut in (900.0, 1200.0, 1500.0, 2500.0):
            state, actions = mlib.b5_decide(state, snap(ut=ut, **warped))
            self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
            self.assertEqual(actions, [])

    def test_correction_burn_alignment_giveup_consumes_round(self):
        """DIY burner: if the attitude gate never opens (AP wedged / never
        aligned), the round is given up cleanly at burnNoStartSeconds -- cut +
        disengage + clear -- instead of flaking the mission."""
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, apoapsis=11_480_000.0,
                                                    periapsis=76_000.0, body="Kerbin",
                                                    node_count=1))
        self.assertEqual(actions, [Action(mlib.ACTION_AP_POINT_NODE)])
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        # Never aligned (ap_error NaN default fails closed): tolerated up to
        # the give-up bound; the flip engages physics warp once and holds it.
        pre = dict(apoapsis=11_480_000.0, periapsis=76_000.0, body="Kerbin",
                   node_count=1)
        state, actions = mlib.b5_decide(state, snap(ut=20.0, **pre))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 1.0)])
        for ut in (400.0, 600.0):
            state, actions = mlib.b5_decide(state, snap(
                ut=ut, warp_mode="PHYSICS", warp_rate=2.0, **pre))
            self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
            self.assertEqual(actions, [])
        # ...then given up: round consumed, full cleanup INCLUDING dropping
        # the flip's physics warp on the way out.
        state, actions = mlib.b5_decide(state, snap(
            ut=611.0, warp_mode="PHYSICS", warp_rate=2.0, **pre))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)
        self.assertEqual(state.phys_warp_cmd, 0)
        self.assertEqual(actions, [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                   Action(mlib.ACTION_AP_DISENGAGE),
                                   Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0),
                                   Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])

    def test_correction_burn_overshoot_cuts(self):
        """DIY burner: a RISING remaining node dv (burning past the vector)
        cuts immediately even though the cut threshold was never reached."""
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin", node_count=1))
        state, _ = mlib.b5_decide(state, snap(ut=20.0, body="Kerbin",
                                              node_count=1, node_dv=50.0,
                                              ap_error=1.0))     # streak 1 of 2 (flip phys warp)
        state, actions = mlib.b5_decide(state, snap(ut=25.0, body="Kerbin",
                                                    node_count=1, node_dv=50.0,
                                                    ap_error=1.0,
                                                    warp_mode="PHYSICS",
                                                    warp_rate=2.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0)])
        state, actions = mlib.b5_decide(state, snap(ut=26.0, body="Kerbin",
                                                    node_count=1, node_dv=50.0,
                                                    ap_error=1.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 0.25)])
        state, actions = mlib.b5_decide(state, snap(ut=30.0, body="Kerbin",
                                                    node_count=1, node_dv=8.0,
                                                    ap_error=1.0))
        self.assertEqual(actions, [])
        # dv rising well past the observed minimum: overshoot -> cut.
        state, actions = mlib.b5_decide(state, snap(ut=35.0, body="Kerbin",
                                                    node_count=1, node_dv=9.0,
                                                    ap_error=1.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)
        self.assertEqual(actions, [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                   Action(mlib.ACTION_AP_DISENGAGE),
                                   Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])

    def test_correction_burn_aim_then_warp_happy_path(self):
        """OPERATOR PR GATE (no-1x-coast): the correction burn AIMS first
        (2x-physics flip + aligned debounce), then natively warps to
        node_ut - nodeArrivalMarginSeconds (rails freezes orientation),
        re-verifies the streak on arrival, and throttles -- the old 1x wait
        to the node is gone."""
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin",
                                                    node_count=1, node_ut=2000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_AP_POINT_NODE)])
        # Flip at 2x physics while settling (streak 1).
        state, actions = mlib.b5_decide(state, snap(ut=15.0, body="Kerbin",
                                                    node_count=1, node_ut=2000.0,
                                                    node_dv=50.0, ap_error=1.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 1.0)])
        # Gate opens: physics warp dropped first.
        state, actions = mlib.b5_decide(state, snap(ut=25.0, body="Kerbin",
                                                    node_count=1, node_ut=2000.0,
                                                    node_dv=50.0, ap_error=1.0,
                                                    warp_mode="PHYSICS",
                                                    warp_rate=2.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0)])
        # AIM DONE at 1x: warp natively to node_ut - 15; streak resets so the
        # arrival re-earns the debounce.
        state, actions = mlib.b5_decide(state, snap(ut=26.0, body="Kerbin",
                                                    node_count=1, node_ut=2000.0,
                                                    node_dv=50.0, ap_error=1.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 1985.0)])
        self.assertEqual(state.warp_to_cmd, 1985.0)
        self.assertEqual(state.aligned_streak, 0)
        # Mid-warp: silent hold (no flip, no rails, no give-up ticking).
        state, actions = mlib.b5_decide(state, snap(ut=1000.0, body="Kerbin",
                                                    node_count=1, node_ut=2000.0,
                                                    node_dv=50.0, ap_error=1.0,
                                                    warping_to=1985.0,
                                                    warp_mode="RAILS",
                                                    warp_rate=1000.0))
        self.assertEqual(actions, [])
        # ARRIVAL: command clears, no-start clock re-anchors, and the
        # re-verify runs the flip machinery for the fresh debounce (rails
        # held the attitude, so this is the documented 2-frame churn).
        state, actions = mlib.b5_decide(state, snap(ut=1990.0, body="Kerbin",
                                                    node_count=1, node_ut=2000.0,
                                                    node_dv=50.0, ap_error=1.0))
        self.assertIsNone(state.warp_to_cmd)
        self.assertEqual(state.corr_nostart_anchor_ut, 1990.0)
        self.assertEqual(state.aligned_streak, 1)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 1.0)])
        state, actions = mlib.b5_decide(state, snap(ut=1991.0, body="Kerbin",
                                                    node_count=1, node_ut=2000.0,
                                                    node_dv=50.0, ap_error=1.0,
                                                    warp_mode="PHYSICS",
                                                    warp_rate=2.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0)])
        # Re-verified at 1x inside the margin: throttle (no re-warp -- the
        # node is within the arrival margin now).
        state, actions = mlib.b5_decide(state, snap(ut=1992.0, body="Kerbin",
                                                    node_count=1, node_ut=2000.0,
                                                    node_dv=50.0, ap_error=1.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 0.25)])
        self.assertTrue(state.corr_burn_started)

    def test_correction_burn_aim_warp_nostart_anchor_and_drift_reflip(self):
        """The no-start give-up must NOT fire on arrival after a long warp
        (warp time is not alignment time), and a drifted attitude on arrival
        re-enters the 2x-physics flip bounded by the re-anchored give-up."""
        state = _b5_state(mlib.B5_CORRECTION_BURN,
                          warp_to_cmd=3000.0, last_warp_issue_ut=20.0,
                          phase_entry_ut=0.0)
        # Arrival 3010 game-s after phase entry (5x burnNoStartSeconds):
        # DRIFTED attitude -> no give-up, the flip re-engages instead.
        state, actions = mlib.b5_decide(state, snap(ut=3010.0, body="Kerbin",
                                                    node_count=1, node_ut=3015.0,
                                                    node_dv=50.0, ap_error=100.0))
        self.assertFalse(state.done)
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        self.assertIsNone(state.warp_to_cmd)
        self.assertEqual(state.corr_nostart_anchor_ut, 3010.0)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_PHYSICS_WARP, 1.0)])
        # Still misaligned 590 s after the ARRIVAL anchor: not yet.
        state, actions = mlib.b5_decide(state, snap(ut=3600.0, body="Kerbin",
                                                    node_count=1, node_ut=3015.0,
                                                    node_dv=50.0, ap_error=100.0,
                                                    warp_mode="PHYSICS",
                                                    warp_rate=2.0))
        self.assertFalse(state.done)
        # 601 s after the anchor: the give-up fires (round consumed).
        state, actions = mlib.b5_decide(state, snap(ut=3611.0, body="Kerbin",
                                                    node_count=1, node_ut=3015.0,
                                                    node_dv=50.0, ap_error=100.0,
                                                    warp_mode="PHYSICS",
                                                    warp_rate=2.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)

    def test_correction_burn_node_vanish_mid_warp_cancels(self):
        """A node that vanishes while the aim-warp is in flight exits the
        round cleanly AND cancels the native warp."""
        state = _b5_state(mlib.B5_CORRECTION_BURN,
                          warp_to_cmd=5000.0, last_warp_issue_ut=20.0)
        state, actions = mlib.b5_decide(state, snap(ut=1000.0, body="Kerbin",
                                                    node_count=0,
                                                    warping_to=5000.0,
                                                    warp_mode="RAILS",
                                                    warp_rate=1000.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)
        self.assertIsNone(state.warp_to_cmd)
        self.assertEqual(actions, [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                   Action(mlib.ACTION_AP_DISENGAGE),
                                   Action(mlib.ACTION_CANCEL_WARP)])

    def test_plan_phase_holds_rails_warp_between_attempts(self):
        """OPERATOR PR GATE: plan phases ride the legality-clamped
        planWarpFactor (10x) between attempts -- never 1x."""
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=100.0,
                          phase_entry_ut=100.0)
        # First frame after entry (no cadence due): the hold is commanded.
        state, actions = mlib.b5_decide(state, snap(ut=105.0, altitude=90_000.0,
                                                    body="Kerbin"))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 2.0)])
        # Held silently while the game rails at 10x.
        state, actions = mlib.b5_decide(state, snap(ut=107.0, altitude=95_000.0,
                                                    body="Kerbin",
                                                    warp_mode="RAILS",
                                                    warp_rate=10.0))
        self.assertEqual(actions, [])

    def test_no_1x_coast_invariant(self):
        """OPERATOR PR GATE INVARIANT: in COAST-TO-TARGET and TARGET-FLYBY
        the machine never COMMANDS rails factor 0 or 1 except when (a) the
        impact guard is active, (b) a pending node is inside the arrival
        margin or its UT is unknown (fail closed), or (c) the body/altitude
        reading is blank (fail closed). The altitude-legality clamp is a
        hard game constraint and the grid stays at exo altitudes where it
        never binds below 2. Native warp_to_ut emissions are always legal."""
        coast_grid = [
            # rounds pending: trigger stair across the whole approach band
            dict(rounds=0, kw=dict(ut=10.0, altitude=90_000.0, body="Kerbin",
                                   vertical_speed=800.0)),
            dict(rounds=1, kw=dict(ut=10.0, altitude=4_500_000.0, body="Kerbin",
                                   vertical_speed=800.0)),
            dict(rounds=1, kw=dict(ut=10.0, altitude=5_900_000.0, body="Kerbin",
                                   vertical_speed=800.0)),
            dict(rounds=1, kw=dict(ut=10.0, altitude=5_995_000.0, body="Kerbin",
                                   vertical_speed=800.0)),
            dict(rounds=1, kw=dict(ut=10.0, altitude=5_999_900.0, body="Kerbin",
                                   vertical_speed=800.0)),
            # rounds spent: no encounter / far encounter / inside SOI lead
            dict(rounds=2, kw=dict(ut=10.0, altitude=8_000_000.0, body="Kerbin")),
            dict(rounds=2, kw=dict(ut=10.0, altitude=8_000_000.0, body="Kerbin",
                                   time_to_soi=200_000.0)),
            dict(rounds=2, kw=dict(ut=10.0, altitude=10_000_000.0, body="Kerbin",
                                   time_to_soi=25.0)),
            dict(rounds=2, kw=dict(ut=10.0, altitude=10_000_000.0, body="Kerbin",
                                   time_to_soi=5.0)),
            # pending node far out (native warp, no rails)
            dict(rounds=2, kw=dict(ut=10.0, altitude=8_000_000.0, body="Kerbin",
                                   node_count=1, node_ut=50_000.0)),
        ]
        for case in coast_grid:
            state = _b5_state(mlib.B5_COAST_TO_TARGET,
                              correction_rounds_done=case["rounds"])
            state, actions = mlib.b5_decide(state, snap(**case["kw"]))
            if state.phase != mlib.B5_COAST_TO_TARGET:
                continue  # trigger crossings enter PLAN (its own hold)
            for a in actions:
                if a.kind == mlib.ACTION_SET_RAILS_WARP:
                    self.assertGreaterEqual(a.value, 2.0, case)
        flyby_grid = [
            dict(kw=dict(ut=10.0, altitude=2_000_000.0, periapsis=60_000.0,
                         body="Mun")),
            dict(kw=dict(ut=10.0, altitude=61_000.0, periapsis=56_000.0,
                         body="Mun")),
            dict(kw=dict(ut=10.0, altitude=20_000.0, periapsis=15_000.0,
                         body="Mun")),
            dict(kw=dict(ut=10.0, altitude=2_300_000.0, periapsis=60_000.0,
                         body="Mun", time_to_soi=20.0)),
            dict(kw=dict(ut=10.0, altitude=2_000_000.0, periapsis=60_000.0,
                         body="Mun", time_to_soi=8_000.0)),
        ]
        for case in flyby_grid:
            state = _b5_state(mlib.B5_TARGET_FLYBY)
            state, actions = mlib.b5_decide(state, snap(**case["kw"]))
            for a in actions:
                if a.kind == mlib.ACTION_SET_RAILS_WARP:
                    self.assertGreaterEqual(a.value, 2.0, case)
        # The allowed-0 cases stay 0: impact guard...
        state = _b5_state(mlib.B5_TARGET_FLYBY, warp_cmd=5)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, altitude=300_000.0, periapsis=-28_000.0, body="Mun"))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])
        # ...and a pending node with an unknown UT (fail closed).
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2,
                          warp_cmd=6)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, altitude=8_000_000.0, body="Kerbin", node_count=1))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])

    def test_correction_burn_no_progress_gives_up(self):
        """BLOCKER-1 (review): the finding-9b NO-PROGRESS give-up. A started
        burn whose remaining node dv FREEZES (zero thrust despite the throttle
        command -- tenth live flight) is given up after burnStagnantSeconds of
        no progress: exit to COAST, round consumed, full cleanup."""
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin", node_count=1))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        # Flip + gate open + throttle-up (phys warp raised then dropped).
        state, _ = mlib.b5_decide(state, snap(ut=15.0, body="Kerbin", node_count=1,
                                              node_dv=50.0, ap_error=1.0))
        state, _ = mlib.b5_decide(state, snap(ut=25.0, body="Kerbin", node_count=1,
                                              node_dv=50.0, ap_error=1.0,
                                              warp_mode="PHYSICS", warp_rate=2.0))
        state, actions = mlib.b5_decide(state, snap(ut=26.0, body="Kerbin",
                                                    node_count=1, node_dv=50.0,
                                                    ap_error=1.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 0.25)])
        # One frame of real progress re-stamps the anchor (ut=30).
        state, actions = mlib.b5_decide(state, snap(ut=30.0, body="Kerbin",
                                                    node_count=1, node_dv=49.0,
                                                    ap_error=1.0))
        self.assertEqual(actions, [])
        # dv FROZEN at 49: under the 120 s bound, still tolerated...
        state, actions = mlib.b5_decide(state, snap(ut=149.0, body="Kerbin",
                                                    node_count=1, node_dv=49.0,
                                                    ap_error=1.0))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        self.assertEqual(actions, [])
        # ...and given up once 120 s pass with no progress since the anchor.
        state, actions = mlib.b5_decide(state, snap(ut=151.0, body="Kerbin",
                                                    node_count=1, node_dv=49.0,
                                                    ap_error=1.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)
        self.assertEqual(actions, [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                   Action(mlib.ACTION_AP_DISENGAGE),
                                   Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])

    def test_correction_burn_steady_progress_never_gives_up(self):
        """BLOCKER-1 contrast: a strictly-dropping remaining dv re-stamps the
        progress anchor every frame, so the no-progress give-up NEVER fires
        across a span far beyond burnStagnantSeconds; the burn ends at the
        normal cut threshold."""
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin", node_count=1))
        state, _ = mlib.b5_decide(state, snap(ut=15.0, body="Kerbin", node_count=1,
                                              node_dv=400.0, ap_error=1.0))
        state, _ = mlib.b5_decide(state, snap(ut=25.0, body="Kerbin", node_count=1,
                                              node_dv=400.0, ap_error=1.0,
                                              warp_mode="PHYSICS", warp_rate=2.0))
        state, actions = mlib.b5_decide(state, snap(ut=26.0, body="Kerbin",
                                                    node_count=1, node_dv=400.0,
                                                    ap_error=1.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 0.25)])
        # 400 game-s of slow but STEADY progress (dv drops ~1 m/s per frame,
        # frames 40 s apart -- each drop re-stamps the anchor). The apoapsis
        # moves per frame (a live burn moves the apsides), so the separate
        # frozen-telemetry detector never counts these frames.
        dv = 400.0
        for i in range(1, 11):
            dv -= 1.0
            state, actions = mlib.b5_decide(state, snap(
                ut=26.0 + 40.0 * i, body="Kerbin", node_count=1,
                node_dv=dv, ap_error=1.0,
                apoapsis=11_000_000.0 + 10_000.0 * i))
            self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN, i)
            self.assertEqual(actions, [], i)
        # Normal cut still owns the exit.
        state, actions = mlib.b5_decide(state, snap(ut=500.0, body="Kerbin",
                                                    node_count=1, node_dv=1.5,
                                                    ap_error=1.0,
                                                    apoapsis=11_500_000.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(actions, [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                   Action(mlib.ACTION_AP_DISENGAGE),
                                   Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])

    def test_plan_attempt_giveup_correction_falls_through(self):
        """Finding 14: three disqualified/failed plans (node_count stays 0 --
        the machine cannot tell over-cap removal from a no-encounter throw)
        end PLAN-CORRECTION at the NEXT cadence check instead of 1x-idling
        out the 300 s plan budget; the round is consumed."""
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=0)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, altitude=90_000.0,
                                                    body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        self.assertEqual(state.plan_attempts, 1)   # entry emission = attempt 1
        self.assertEqual(actions[-1].kind, mlib.ACTION_MJ_PLAN_COURSE_CORRECT)
        # Cadence re-plans: attempts 2 and 3 (the plan phase HOLDS its 10x
        # rails factor between attempts -- operator PR gate; the RAILS
        # fixture keeps the emission discipline silent).
        state, actions = mlib.b5_decide(state, snap(ut=41.0, altitude=95_000.0,
                                                    body="Kerbin",
                                                    warp_mode="RAILS",
                                                    warp_rate=10.0))
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT,
                                          60000.0, limit=150.0)])
        self.assertEqual(state.plan_attempts, 2)
        state, actions = mlib.b5_decide(state, snap(ut=72.0, altitude=100_000.0,
                                                    body="Kerbin",
                                                    warp_mode="RAILS",
                                                    warp_rate=10.0))
        self.assertEqual(state.plan_attempts, 3)
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT,
                                          60000.0, limit=150.0)])
        # Next cadence with still no node: give up EARLY (~90 s, not 300).
        state, actions = mlib.b5_decide(state, snap(ut=103.0, altitude=105_000.0,
                                                    body="Kerbin",
                                                    warp_mode="RAILS",
                                                    warp_rate=10.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)
        self.assertEqual(actions, [])

    def test_plan_attempt_giveup_transfer_flakes(self):
        """Finding 14: PLAN-TRANSFER takes the same early give-up as a FLAKE
        (no transfer = no mission), not a silent fall-through."""
        state = _b5_state(mlib.B5_ORBIT)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_PLAN_TRANSFER)
        self.assertEqual(state.plan_attempts, 1)
        state, _ = mlib.b5_decide(state, snap(ut=41.0, altitude=80_000.0,
                                              body="Kerbin"))
        state, _ = mlib.b5_decide(state, snap(ut=72.0, altitude=80_000.0,
                                              body="Kerbin", warp_mode="RAILS",
                                              warp_rate=10.0))
        self.assertEqual(state.plan_attempts, 3)
        state, _ = mlib.b5_decide(state, snap(ut=103.0, altitude=80_000.0,
                                              body="Kerbin", warp_mode="RAILS",
                                              warp_rate=10.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.B5_PLAN_TRANSFER)

    def test_plan_attempt_three_with_node_still_hands_off(self):
        """Finding 14: a node appearing on (or after) attempt 3 still hands
        off to the burner normally -- the give-up only fires while
        node_count is 0 at a cadence check."""
        state = _b5_state(mlib.B5_ORBIT)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin"))
        state, _ = mlib.b5_decide(state, snap(ut=41.0, altitude=80_000.0,
                                              body="Kerbin"))
        state, _ = mlib.b5_decide(state, snap(ut=72.0, altitude=80_000.0,
                                              body="Kerbin", warp_mode="RAILS",
                                              warp_rate=10.0))
        self.assertEqual(state.plan_attempts, 3)
        state, actions = mlib.b5_decide(state, snap(ut=90.0, body="Kerbin",
                                                    node_count=1))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_EXECUTE_NODES)])

    def test_transfer_burn_stagnation_under_floor_flakes(self):
        """A wedged executor after an UNDER-FLOOR TLI (no transfer to coast on)
        is a bounded flake, never a silent coast to nowhere."""
        state = _b5_state(mlib.B5_PLAN_TRANSFER, last_plan_ut=0.0)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, apoapsis=84_000.0,
                                              periapsis=79_000.0, body="Kerbin",
                                              node_count=1))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        # Partial burn to 5M (under the 10M floor), then wedged static.
        wedged = dict(apoapsis=5_000_000.0, periapsis=76_000.0, body="Kerbin",
                      node_count=1)
        state, _ = mlib.b5_decide(state, snap(ut=20.0, **wedged))
        state, _ = mlib.b5_decide(state, snap(ut=30.0, **wedged))
        state, _ = mlib.b5_decide(state, snap(ut=160.0, **wedged))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.B5_TRANSFER_BURN)

    def test_coast_skips_correction_when_disabled(self):
        # courseCorrectPeriapsisMeters=0 disables the rounds entirely: the first
        # coast frame warps instead of entering PLAN-CORRECTION. At 90 km over
        # Kerbin the stock altitude table legalizes only 50x (factor 3): the
        # command is pre-clamped to the ACHIEVABLE factor, never the raw cap.
        params = mlib.B5Params(**{**B5_PARAMS.__dict__, "course_correct_periapsis": 0.0})
        base = mlib.b5_initial_state(params)
        state = base.__class__(**{**base.__dict__, "phase": mlib.B5_COAST_TO_TARGET,
                                  "phase_entry_ut": 0.0})
        state, actions = mlib.b5_decide(state, snap(ut=30.0, apoapsis=11_000_000.0,
                                                    altitude=90_000.0, body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 3.0)])
        # High over Kerbin (>= 600 km) everything is legal: the full cap flows.
        state, actions = mlib.b5_decide(state, snap(ut=40.0, apoapsis=11_000_000.0,
                                                    altitude=8_000_000.0,
                                                    body="Kerbin", warp_mode="RAILS",
                                                    warp_rate=50.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])

    def test_coast_correction_rounds_trigger_by_altitude(self):
        # Round 1 (trigger 0) fires on the first coast frame; after it is done,
        # the coast warps BELOW trigger 2's slow-down band and enters round 2
        # at/above the trigger (dropping warp first); with both rounds spent,
        # the coast holds full warp with NO re-emission.
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1)
        # 200 km: the trigger stair wants the full cap but the stock Kerbin
        # table legalizes only 100x (factor 4) there -- command the achievable.
        state, actions = mlib.b5_decide(state, snap(ut=10.0, altitude=200_000.0,
                                                    body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 4.0)])
        state, actions = mlib.b5_decide(state, snap(ut=20.0, altitude=6_200_000.0,
                                                    body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        # Prelude steps the held factor straight to the PLAN rails hold
        # (never 1x -- operator PR gate).
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 2.0),
                                   Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT, 60000.0,
                                          limit=150.0)])
        # Both rounds spent: full warp emitted once, then held silently.
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        state, actions = mlib.b5_decide(state, snap(ut=30.0, altitude=8_000_000.0,
                                                    body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])
        state, actions = mlib.b5_decide(state, snap(ut=40.0, altitude=8_400_000.0,
                                                    body="Kerbin", warp_mode="RAILS",
                                                    warp_rate=1000.0))
        self.assertEqual(actions, [])   # factor held AND game warping: NO emission

    def test_coast_reasserts_warp_when_game_dropped_it(self):
        # Fifteenth flight: manual warp changes (or KSP's own drops) override
        # the held factor; the on-change discipline must re-assert whenever the
        # game is not rails-warping despite a nonzero command.
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2,
                          warp_cmd=6)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, altitude=8_000_000.0,
                                                    body="Kerbin"))  # warp NONE
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])

    def test_coast_stairs_warp_down_toward_trigger(self):
        """Operator-reported bug (sixteenth flight round): the old binary
        slow-down band held 1x for its ENTIRE 2,000 km (~40 real minutes at
        coast speeds). The factor now STAIRS DOWN with remaining distance:
        1000x far out, reduced factors approaching, 1x only in the last
        moments."""
        # Far below the 6M trigger at 800 m/s: 1000x (index 5) fits; the full
        # 10,000x cap would overshoot the remaining 1.5M in one safety window.
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1,
                          warp_cmd=0)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, altitude=4_500_000.0, vertical_speed=800.0, body="Kerbin"))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 5.0)])
        # ~100 km out: 1000x would overshoot; 100x fits.
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, altitude=5_900_000.0, vertical_speed=800.0, body="Kerbin",
            warp_mode="RAILS", warp_rate=1000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 4.0)])
        # ~5 km out: the raw stair says 5x, but the no-1x-coast FLOOR keeps
        # 10x (operator PR gate: a trigger is a refinement point, not a
        # wall; overshoot at 10x is <= ~5 game-s per poll).
        state, actions = mlib.b5_decide(state, snap(
            ut=30.0, altitude=5_995_000.0, vertical_speed=800.0, body="Kerbin",
            warp_mode="RAILS", warp_rate=100.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 2.0)])
        # A few hundred metres out: still the factor-2 floor, held silently.
        state, actions = mlib.b5_decide(state, snap(
            ut=40.0, altitude=5_999_600.0, vertical_speed=800.0, body="Kerbin",
            warp_mode="RAILS", warp_rate=10.0))
        self.assertEqual(actions, [])

    def test_rails_factor_for_distance_pure(self):
        # Index map: 5 = 1000x, 6 = 10,000x (RAILS_WARP_RATES).
        self.assertEqual(mlib.rails_factor_for_distance(9_000_000, 800.0, 6), 6)
        self.assertEqual(mlib.rails_factor_for_distance(5_800_000, 800.0, 6), 5)
        self.assertEqual(mlib.rails_factor_for_distance(100_000, 800.0, 6), 4)
        self.assertEqual(mlib.rails_factor_for_distance(5_000, 800.0, 6), 1)
        self.assertEqual(mlib.rails_factor_for_distance(500, 800.0, 6), 0)
        self.assertEqual(mlib.rails_factor_for_distance(-100, 800.0, 6), 0)
        self.assertEqual(mlib.rails_factor_for_distance(float("nan"), 800.0, 6), 0)
        # Tiny/zero closure speed floors at 10 m/s (conservative).
        self.assertEqual(mlib.rails_factor_for_distance(50_000, 0.0, 6), 5)
        # The cap is honored.
        self.assertEqual(mlib.rails_factor_for_distance(5_800_000, 800.0, 3), 3)

    def test_rails_factor_for_time_pure(self):
        # Highest factor whose rate * 1 s safety window fits the remaining
        # game time. Index map: 5 = 1000x, 6 = 10,000x, 7 = 100,000x.
        self.assertEqual(mlib.rails_factor_for_time(2_000_000.0, 7), 7)
        self.assertEqual(mlib.rails_factor_for_time(50_000.0, 7), 6)
        self.assertEqual(mlib.rails_factor_for_time(50_000.0, 6), 6)
        self.assertEqual(mlib.rails_factor_for_time(5_000.0, 6), 5)
        self.assertEqual(mlib.rails_factor_for_time(30.0, 6), 2)
        self.assertEqual(mlib.rails_factor_for_time(3.0, 6), 0)
        # Fail closed: unknown / non-positive remaining time never warps.
        self.assertEqual(mlib.rails_factor_for_time(0.0, 6), 0)
        self.assertEqual(mlib.rails_factor_for_time(-10.0, 6), 0)
        self.assertEqual(mlib.rails_factor_for_time(float("nan"), 6), 0)
        # The cap is honored.
        self.assertEqual(mlib.rails_factor_for_time(2_000_000.0, 5), 5)

    def test_max_legal_rails_factor_pure(self):
        # Ground-truth stock tables (extracted from the dev install's
        # serialized CelestialBody.timeWarpAltitudeLimits; see mlib).
        # Kerbin: 80 km parking orbit legalizes only 50x (factor 3) -- the
        # live-observed clamp that silently ran a commanded 10,000x at 50x.
        self.assertEqual(mlib.max_legal_rails_factor("Kerbin", 80_000.0), 3)
        self.assertEqual(mlib.max_legal_rails_factor("Kerbin", 119_999.0), 3)
        self.assertEqual(mlib.max_legal_rails_factor("Kerbin", 120_000.0), 4)
        self.assertEqual(mlib.max_legal_rails_factor("Kerbin", 240_000.0), 5)
        self.assertEqual(mlib.max_legal_rails_factor("Kerbin", 480_000.0), 6)
        self.assertEqual(mlib.max_legal_rails_factor("Kerbin", 600_000.0), 7)
        self.assertEqual(mlib.max_legal_rails_factor("Kerbin", 8_000_000.0), 7)
        # Legality is altitude >= limit (kRPC CanRailsWarpAt rejects strictly
        # below), so the boundary value itself is legal.
        self.assertEqual(mlib.max_legal_rails_factor("Mun", 25_000.0), 4)
        self.assertEqual(mlib.max_legal_rails_factor("Mun", 24_999.0), 3)
        self.assertEqual(mlib.max_legal_rails_factor("Minmus", 60_000.0), 7)
        self.assertEqual(mlib.max_legal_rails_factor("Minmus", 5_000.0), 2)
        self.assertEqual(mlib.max_legal_rails_factor("Duna", 100_000.0), 4)
        self.assertEqual(mlib.max_legal_rails_factor("Sun", 13_500_000_000.0), 7)
        # Fail OPEN (top factor) for unknown bodies / non-finite altitude: the
        # server clamp is the backstop, and a one-frame altitude blip must not
        # sawtooth a held warp down to 1x.
        self.assertEqual(mlib.max_legal_rails_factor("ModdedBody", 1_000.0), 7)
        self.assertEqual(mlib.max_legal_rails_factor("", 1_000.0), 7)
        self.assertEqual(mlib.max_legal_rails_factor("Kerbin", float("nan")), 7)

    def test_coast_warp_gated_on_home_body_and_no_nodes(self):
        # rounds_done=2: both correction rounds spent, pure warp management.
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        # A pending node with an UNKNOWN UT (NaN, fail closed) keeps warp DOWN
        # (never warp past a maneuver on no evidence); factor already 0 -> no
        # emission.
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin",
                                                    altitude=8_000_000.0,
                                                    node_count=1))
        self.assertEqual(actions, [])
        # Clean coast: full factor emitted on change.
        state, actions = mlib.b5_decide(state, snap(ut=20.0, body="Kerbin",
                                                    altitude=8_000_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])

    def test_coast_native_warps_toward_pending_node(self):
        """Path A: a pending node issues ONE native warp_to_ut toward
        node_ut - nodeWarpLeadSeconds; while the game reports the warp active
        the machine emits nothing (and never a rails factor); past the target
        the command clears with no action; a NaN node_ut never warps."""
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        # Node 40,000 s out: one native warp to node_ut - 15 (the arrival
        # margin; nodeWarpLeadSeconds is retired -- operator PR gate: the
        # burn phase aims BEFORE warping, so no flip window is needed).
        state, actions = mlib.b5_decide(state, snap(
            ut=1000.0, body="Kerbin", altitude=8_000_000.0,
            node_count=1, node_ut=41_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 40_985.0)])
        self.assertEqual(state.warp_to_cmd, 40_985.0)
        self.assertEqual(state.warp_cmd, 0)
        # Warp active (warping_to = target): silent hold, no rails emission.
        state, actions = mlib.b5_decide(state, snap(
            ut=20_000.0, body="Kerbin", altitude=8_000_000.0,
            node_count=1, node_ut=41_000.0, warping_to=40_985.0,
            warp_mode="RAILS", warp_rate=10_000.0))
        self.assertEqual(actions, [])
        # Past the target (inside the arrival margin): command clears, no
        # action needed (the server stepper zeroed the factor on arrival).
        state, actions = mlib.b5_decide(state, snap(
            ut=40_990.0, body="Kerbin", altitude=8_000_000.0,
            node_count=1, node_ut=41_000.0))
        self.assertEqual(actions, [])
        self.assertIsNone(state.warp_to_cmd)
        # NaN node_ut (fail closed): no warp of any kind.
        fresh = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        fresh, actions = mlib.b5_decide(fresh, snap(
            ut=10.0, body="Kerbin", altitude=8_000_000.0, node_count=1))
        self.assertEqual(actions, [])
        self.assertIsNone(fresh.warp_to_cmd)

    def test_coast_native_warp_self_heal_is_bounded(self):
        """Self-healing: warping_to NaN while the commanded target is still
        ahead re-issues the SAME warp, at most once per 30 game-s."""
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2,
                          warp_to_cmd=40_985.0, last_warp_issue_ut=1000.0)
        # 10 game-s after the issue: inside the re-issue bound, no spam.
        state, actions = mlib.b5_decide(state, snap(
            ut=1010.0, body="Kerbin", altitude=8_000_000.0,
            node_count=1, node_ut=41_000.0))
        self.assertEqual(actions, [])
        # 35 game-s after the issue and still no active warp: re-issue once.
        state, actions = mlib.b5_decide(state, snap(
            ut=1035.0, body="Kerbin", altitude=8_000_000.0,
            node_count=1, node_ut=41_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 40_985.0)])
        self.assertEqual(state.last_warp_issue_ut, 1035.0)

    def test_coast_soi_native_warp_and_retarget_threshold(self):
        """Path A (b): the post-correction coast issues a native warp to
        now + time_to_soi - soiLeadSeconds; small SOI-estimate drift holds,
        a shift beyond 120 s re-issues; inside the lead window the rails
        fallback (time stair floored at the flyby factor) takes over."""
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        # Encounter 200,000 s out: one native warp to boundary minus 30 s.
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", altitude=8_000_000.0, time_to_soi=200_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 199_980.0)])
        # Estimate drift under the 120 s threshold: hold (warp active).
        state, actions = mlib.b5_decide(state, snap(
            ut=5_000.0, body="Kerbin", altitude=9_000_000.0,
            time_to_soi=195_010.0, warping_to=199_980.0,
            warp_mode="RAILS", warp_rate=10_000.0))
        self.assertEqual(actions, [])
        # Estimate shifted ~460 s: re-issue at the fresh target.
        state, actions = mlib.b5_decide(state, snap(
            ut=6_000.0, body="Kerbin", altitude=9_000_000.0,
            time_to_soi=193_550.0, warping_to=199_980.0,
            warp_mode="RAILS", warp_rate=10_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 199_520.0)])
        self.assertEqual(state.warp_to_cmd, 199_520.0)
        # Inside the 30 s lead window: rails fallback -- time stair floored
        # at the flyby factor (never a 1x cliff at the boundary). Fresh state
        # so the emission is observable without a preceding cancel.
        fresh = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        fresh, actions = mlib.b5_decide(fresh, snap(
            ut=30.0, body="Kerbin", altitude=10_000_000.0, time_to_soi=25.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 5.0)])

    def test_coast_rails_intent_cancels_active_native_warp_first(self):
        """Never two warp writers: when the machine wants a rails factor but
        a native warp is still (expected) active -- e.g. the encounter was
        lost mid-warp -- it emits CANCEL first and the rails command follows
        on the next poll."""
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2,
                          warp_to_cmd=500_000.0, last_warp_issue_ut=10.0)
        # Encounter gone (tts NaN): rails fallback wanted, native active ->
        # CANCEL only this frame.
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, body="Kerbin", altitude=8_000_000.0,
            warping_to=500_000.0, warp_mode="RAILS", warp_rate=10_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_CANCEL_WARP)])
        self.assertIsNone(state.warp_to_cmd)
        self.assertEqual(state.warp_cmd, 0)
        # Next poll (warp idle): the rails coast factor flows.
        state, actions = mlib.b5_decide(state, snap(
            ut=21.0, body="Kerbin", altitude=8_000_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])

    def test_coast_trigger_prelude_cancels_native_warp(self):
        """Crossing a correction trigger with a native warp active cancels
        it in the prelude (instead of the rails-0 prelude) before planning."""
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1,
                          warp_to_cmd=777_777.0)
        state, actions = mlib.b5_decide(state, snap(
            ut=100.0, body="Kerbin", altitude=6_200_000.0,
            warping_to=777_777.0, warp_mode="RAILS", warp_rate=1000.0))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        self.assertIsNone(state.warp_to_cmd)
        self.assertEqual(actions, [Action(mlib.ACTION_CANCEL_WARP),
                                   Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT, 60000.0,
                                          limit=150.0)])

    def test_coast_empty_body_stays_without_hop(self):
        # "" = no reading this frame: NOT the ejected terminal, NOT a hop.
        state = _b5_state(mlib.B5_COAST_TO_TARGET)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body=""))
        self.assertFalse(state.done)
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(actions, [])

    def test_coast_blank_body_dwell_is_bounded(self):
        """Review SF-2: a PERSISTENT body=="" hold is bounded -- at
        frozen_sample_limit consecutive blanks the vessel is declared lost
        (the old unbounded 1x hold could idle ~111 wall-hours against the
        GAME-time coast budget). A real body reading resets the dwell."""
        limit = B5_PARAMS.frozen_sample_limit
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        # 5 blanks, then a real reading: the dwell resets.
        for i in range(5):
            state, _ = mlib.b5_decide(state, snap(
                ut=10.0 + i, altitude=100_000.0 + i, body=""))
        self.assertEqual(state.body_blank_count, 5)
        state, _ = mlib.b5_decide(state, snap(ut=20.0, altitude=8_000_000.0,
                                              body="Kerbin"))
        self.assertEqual(state.body_blank_count, 0)
        self.assertFalse(state.done)
        # limit consecutive blanks: vessel-lost terminal. Altitudes vary per
        # frame so the (1x-gated) frozen detector is not the tripping party.
        for i in range(limit - 1):
            state, _ = mlib.b5_decide(state, snap(
                ut=30.0 + i, altitude=200_000.0 + i, body=""))
            self.assertFalse(state.done, i)
        state, _ = mlib.b5_decide(state, snap(
            ut=30.0 + limit, altitude=300_000.0, body=""))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("unreadable", state.loss_reason)

    def test_flyby_blank_body_dwell_is_bounded(self):
        """Review SF-2, the FLYBY side of the blank-body dwell bound."""
        limit = B5_PARAMS.frozen_sample_limit
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        for i in range(limit - 1):
            state, _ = mlib.b5_decide(state, snap(
                ut=10.0 + i, altitude=500_000.0 + i, body=""))
            self.assertFalse(state.done, i)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0 + limit, altitude=600_000.0, body=""))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("unreadable", state.loss_reason)

    def test_rails_over_warp_pull_down(self):
        """Review SF-1: the self-heal is now bidirectional. The game rails-
        warping FASTER than the desired factor (manual warp-up / stale rate)
        re-commands the desired factor -- including desired 0."""
        # Held factor 4 (100x legal ceiling at 200 km) but the game reads
        # RAILS x1000: re-command 4.
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1,
                          warp_cmd=4)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, altitude=200_000.0, body="Kerbin",
            warp_mode="RAILS", warp_rate=1000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 4.0)])
        # Desired 0 (pending node, unknown UT) while the game rails at 50x:
        # pull down to 1x even though warp_cmd is already 0.
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, altitude=8_000_000.0, body="Kerbin", node_count=1,
            warp_mode="RAILS", warp_rate=50.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])
        # In-tolerance rate at the commanded factor: NO emission (the 1%
        # allowance ignores ramp jitter).
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2,
                          warp_cmd=6)
        state, actions = mlib.b5_decide(state, snap(
            ut=30.0, altitude=8_000_000.0, body="Kerbin",
            warp_mode="RAILS", warp_rate=10_000.0))
        self.assertEqual(actions, [])

    def test_native_warp_exempt_from_over_warp_pull_down(self):
        """Review SF-1: while a native warp_to_ut is commanded/active the
        game legitimately runs rates the rails stair never commanded -- the
        pull-down must NOT fire (no rails emission at all)."""
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2,
                          warp_to_cmd=150_000.0, last_warp_issue_ut=10.0)
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, altitude=8_000_000.0, body="Kerbin",
            time_to_soi=150_040.0, warping_to=150_000.0,
            warp_mode="RAILS", warp_rate=10_000.0))
        self.assertEqual(actions, [])
        self.assertEqual(state.warp_to_cmd, 150_000.0)

    def test_frozen_detector_ignores_warped_frames(self):
        """Review N-A4: the frozen-telemetry detector only advances at 1x. A
        (near-)circular orbit on RAILS can legitimately report bit-identical
        apsides while UT advances -- 12 constant-orbit frames under RAILS
        must NOT trip the vessel-lost terminal."""
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2,
                          warp_cmd=6)
        fields = dict(altitude=8_000_000.0, vertical_speed=100.0,
                      apoapsis=11_000_000.0, periapsis=79_000.0, body="Kerbin",
                      warp_mode="RAILS", warp_rate=10_000.0)
        for i in range(B5_PARAMS.frozen_sample_limit + 2):
            state, _ = mlib.b5_decide(state, snap(ut=10.0 + i, **fields))
            self.assertFalse(state.done, i)
        self.assertEqual(state.frozen_count, 0)

    def test_flyby_non_finite_altitude_forces_1x(self):
        """Review N-A5: fail-closed symmetry -- with no altitude reading the
        flyby stair and legality clamp are both blind, so the factor drops
        to 1x instead of riding the floor."""
        state = _b5_state(mlib.B5_TARGET_FLYBY, warp_cmd=5)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, altitude=float("nan"), periapsis=60_000.0, body="Mun",
            warp_mode="RAILS", warp_rate=100.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])
        self.assertFalse(state.done)

    def test_coast_foreign_body_is_ejected_assert_fail(self):
        state = _b5_state(mlib.B5_COAST_TO_TARGET)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Sun"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("left home SOI", state.loss_reason)
        self.assertIn("Sun", state.loss_reason)

    def test_flyby_tracks_min_altitude_and_returns_home(self):
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, altitude=500_000.0, body="Mun"))
        state, _ = mlib.b5_decide(state, snap(ut=20.0, altitude=65_000.0, body="Mun"))
        state, _ = mlib.b5_decide(state, snap(ut=30.0, altitude=200_000.0, body="Mun"))
        self.assertEqual(state.min_target_altitude, 65_000.0)
        state, actions = mlib.b5_decide(state, snap(ut=40.0, altitude=3_000_000.0,
                                                    body="Kerbin"))
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B5_RETURN)
        self.assertIsNone(state.verdict)
        # RETURN entry drops warp for the settle tail (a factor was held).
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])

    def test_flyby_never_warps_toward_a_known_impact(self):
        # Flight 4 (2026-07-21): warping into a sub-surface-periapsis crash
        # wedges the blocking warp_to under the paused Flight Results dialog
        # for the rest of the mission budget. Low + impact-bound -> poll at 1x
        # (the vessel-lost detectors then end the crash cleanly in seconds).
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        # High altitude, impact periapsis: still warp (plenty of room); far
        # from periapsis the stair runs ABOVE the 100x floor.
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, altitude=800_000.0, periapsis=-28_000.0, body="Mun"))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])
        # Below the guard altitude with a sub-surface periapsis: drop to 1x.
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, altitude=300_000.0, periapsis=-28_000.0, body="Mun"))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])
        self.assertFalse(state.done)
        # Same altitude with a POSITIVE periapsis: warp again (239 km above
        # the periapsis still fits the 10,000x stair at the floored speed).
        state, actions = mlib.b5_decide(state, snap(
            ut=30.0, altitude=300_000.0, periapsis=61_000.0, body="Mun"))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])

    def test_flyby_stair_floors_at_flyby_factor_and_altitude_clamps(self):
        """TARGET-FLYBY factor policy: stair up toward flybyMaxWarpFactor far
        above periapsis, hold the proven flybyWarpFactor (100x evidence
        cadence) near periapsis, and never command over the stock Mun
        altitude-limit table (100x needs >= 25 km, 1000x >= 50 km)."""
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        # Near periapsis (5 km above it): stair collapses, the 100x floor
        # holds, and 61 km altitude legalizes exactly factor 5 on the Mun.
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, altitude=61_000.0, periapsis=56_000.0, body="Mun"))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 5.0)])
        # Descending at 20 km altitude: 100x is NOT legal below 25 km on the
        # Mun -- command the achievable 50x instead of fighting the clamp.
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, altitude=20_000.0, periapsis=15_000.0, body="Mun",
            warp_mode="RAILS", warp_rate=100.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 3.0)])

    def test_flyby_outer_leg_native_warps_to_soi_exit(self):
        """Path A (c): the flyby outer legs issue one native warp_to_ut to
        the SOI EXIT minus soiLeadSeconds; the game's own altitude limits
        shape the periapsis passage. Inside the lead window the rails stair
        fallback takes over."""
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        state, actions = mlib.b5_decide(state, snap(
            ut=100.0, altitude=2_000_000.0, periapsis=60_000.0, body="Mun",
            time_to_soi=8_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 8_070.0)])
        self.assertEqual(state.warp_to_cmd, 8_070.0)
        # Active warp: silent hold (never a rails emission alongside).
        state, actions = mlib.b5_decide(state, snap(
            ut=2_000.0, altitude=500_000.0, periapsis=60_000.0, body="Mun",
            time_to_soi=6_090.0, warping_to=8_070.0,
            warp_mode="RAILS", warp_rate=1000.0))
        self.assertEqual(actions, [])
        # Near the exit (tts <= lead): fallback rails stair. Fresh state so
        # the emission is observable without a preceding cancel frame.
        fresh = _b5_state(mlib.B5_TARGET_FLYBY)
        fresh, actions = mlib.b5_decide(fresh, snap(
            ut=9_000.0, altitude=2_300_000.0, periapsis=60_000.0, body="Mun",
            time_to_soi=20.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])

    def test_flyby_impact_guard_cancels_native_warp(self):
        """The impact guard stays AUTHORITATIVE over the native warp: a
        sub-surface periapsis below the guard altitude cancels an active
        warp_to_ut immediately, then holds 1x under live telemetry."""
        state = _b5_state(mlib.B5_TARGET_FLYBY, warp_to_cmd=99_999.0,
                          last_warp_issue_ut=10.0)
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, altitude=300_000.0, periapsis=-28_000.0, body="Mun",
            time_to_soi=5_000.0, warping_to=99_999.0,
            warp_mode="RAILS", warp_rate=1000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_CANCEL_WARP)])
        self.assertIsNone(state.warp_to_cmd)
        self.assertFalse(state.done)
        # Warp idle next poll: 1x holds silently (warp_cmd already 0).
        state, actions = mlib.b5_decide(state, snap(
            ut=21.0, altitude=299_000.0, periapsis=-28_000.0, body="Mun",
            time_to_soi=5_000.0))
        self.assertEqual(actions, [])

    def test_return_entry_cancels_native_warp(self):
        """Terminal RETURN entry cancels an active native warp (the settle
        tail must run at the game's own 1x, not inside a leftover warp)."""
        state = _b5_state(mlib.B5_TARGET_FLYBY, warp_to_cmd=90_000.0,
                          last_warp_issue_ut=10.0)
        state, actions = mlib.b5_decide(state, snap(
            ut=80_000.0, altitude=3_000_000.0, body="Kerbin",
            warping_to=90_000.0, warp_mode="RAILS", warp_rate=1000.0))
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B5_RETURN)
        self.assertIsNone(state.warp_to_cmd)
        self.assertEqual(actions, [Action(mlib.ACTION_CANCEL_WARP)])

    def test_flyby_ejection_to_sun_is_assert_fail(self):
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Sun"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("ejected", state.loss_reason)

    def test_vessel_lost_any_phase_is_assert_fail(self):
        for phase in (mlib.B5_TRANSFER_BURN, mlib.B5_COAST_TO_TARGET,
                      mlib.B5_TARGET_FLYBY):
            state = _b5_state(phase)
            state, _ = mlib.b5_decide(state, snap(ut=10.0, vessel_lost=True))
            self.assertTrue(state.done)
            self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
            self.assertIn("vessel-lost", state.loss_reason)

    def test_frozen_telemetry_terminal(self):
        # Bit-identical orbit fields while UT advances, limit frames -> loss.
        state = _b5_state(mlib.B5_COAST_TO_TARGET)
        fields = dict(altitude=500_000.0, vertical_speed=100.0,
                      apoapsis=11_000_000.0, periapsis=79_000.0, body="Kerbin")
        state, _ = mlib.b5_decide(state, snap(ut=1.0, **fields))
        for i in range(B5_PARAMS.frozen_sample_limit):
            state, _ = mlib.b5_decide(state, snap(ut=2.0 + i, **fields))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("frozen", state.loss_reason)

    def test_done_machine_is_idempotent(self):
        state = _b5_state(mlib.B5_RETURN, done=True)
        state2, actions = mlib.b5_decide(state, snap(ut=99.0, body="Kerbin"))
        self.assertIs(state2, state)
        self.assertEqual(actions, [])


class B5ParamTests(unittest.TestCase):
    def test_params_from_dict_full(self):
        p = mlib.b5_params_from_dict({
            "targetApoapsisMeters": 90000, "targetPeriapsisMeters": 85000,
            "apoErrorMeters": 4000, "periErrorMeters": 3000,
            "ascentTimeoutSeconds": 500, "circularizeTimeoutSeconds": 200,
            "targetBodyName": "Minmus", "homeBodyName": "Kerbin",
            "transferMinApoapsisMeters": 40_000_000,
            "courseCorrectPeriapsisMeters": 30000,
            "planTimeoutSeconds": 200, "planRetrySeconds": 15,
            "transferBurnTimeoutSeconds": 5000,
            "coastTimeoutSeconds": 900_000, "flybyTimeoutSeconds": 400_000,
            "coastWarpFactor": 7, "flybyWarpFactor": 4,
            "flybyMaxWarpFactor": 7, "nodeArrivalMarginSeconds": 20,
            "planWarpFactor": 3,
            "flipPhysicsWarpFactor": 2,
            "targetPeriapsisFloorMeters": 20000,
            "frozenTelemetrySamples": 5,
        })
        self.assertEqual(p.target_body, "Minmus")
        self.assertEqual(p.transfer_min_apoapsis, 40_000_000.0)
        self.assertEqual(p.course_correct_periapsis, 30000.0)
        self.assertEqual(p.plan_retry_seconds, 15.0)
        self.assertEqual(p.coast_warp_factor, 7)
        self.assertEqual(p.flyby_warp_factor, 4)
        self.assertEqual(p.flyby_max_warp_factor, 7)
        self.assertEqual(p.node_arrival_margin, 20.0)
        self.assertEqual(p.plan_warp_factor, 3)
        self.assertEqual(p.flip_physics_warp, 2)
        self.assertEqual(p.target_periapsis_floor, 20000.0)
        self.assertEqual(p.frozen_sample_limit, 5)

    def test_params_defaults(self):
        p = mlib.b5_params_from_dict({})
        self.assertEqual(p.target_body, "Mun")
        self.assertEqual(p.home_body, "Kerbin")
        self.assertEqual(p.course_correct_periapsis, 60000.0)
        self.assertEqual(p.target_periapsis_floor, 10000.0)
        self.assertEqual(p.max_correction_dv, 150.0)
        self.assertEqual(p.correction_trigger_alts, (0.0, 6_000_000.0))
        self.assertEqual(p.flyby_max_warp_factor, 6)
        self.assertEqual(p.node_arrival_margin, 15.0)
        self.assertEqual(p.plan_warp_factor, 2)
        self.assertEqual(p.soi_lead, 30.0)
        self.assertEqual(p.plan_retry_seconds, 10.0)
        self.assertEqual(p.flip_physics_warp, 1)

    def test_params_correction_dv_cap_from_dict(self):
        p = mlib.b5_params_from_dict({"maxCorrectionDvMps": 40,
                                      "correctionTriggerAltsMeters": [0, 3_000_000]})
        self.assertEqual(p.max_correction_dv, 40.0)
        self.assertEqual(p.correction_trigger_alts, (0.0, 3_000_000.0))

    def test_max_attitude_error_default_pinned(self):
        # Review N-C3: the 30-degree DIY-burner gate default is deliberate
        # (finding 9c: demanding 5 starves the round; the burn self-corrects
        # from a rough start) -- pin it so a drive-by "tighten the tolerance"
        # edit trips a test.
        self.assertEqual(mlib.b5_params_from_dict({}).max_attitude_error_deg, 30.0)

    def test_attitude_gate_boundary_is_inclusive(self):
        """Review N-C3: the aligned gate is |error| <= max_attitude_error_deg,
        INCLUSIVE at the boundary: 29.9 and exactly 30.0 count toward the
        streak, 30.1 resets it."""
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin", node_count=1))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        state, _ = mlib.b5_decide(state, snap(ut=15.0, body="Kerbin", node_count=1,
                                              node_dv=50.0, ap_error=-29.9))
        self.assertEqual(state.aligned_streak, 1)
        state, _ = mlib.b5_decide(state, snap(ut=16.0, body="Kerbin", node_count=1,
                                              node_dv=50.0, ap_error=30.0,
                                              warp_mode="PHYSICS", warp_rate=2.0))
        self.assertEqual(state.aligned_streak, 2)
        state, _ = mlib.b5_decide(state, snap(ut=17.0, body="Kerbin", node_count=1,
                                              node_dv=50.0, ap_error=30.1,
                                              warp_mode="PHYSICS", warp_rate=2.0))
        self.assertEqual(state.aligned_streak, 0)


class CorrectionPlanClassifierTests(unittest.TestCase):
    """Review SF-3: the runner's plan accept/remove decision is the pure
    mlib.classify_correction_plan; thresholds pinned here."""

    def test_fly_band(self):
        self.assertEqual(mlib.classify_correction_plan(50.0, 150.0, 0.5),
                         mlib.PLAN_FLY)
        # Cap boundary is INCLUSIVE-fly (dv == cap flies; only strictly
        # greater disqualifies -- the live comparison this extracts).
        self.assertEqual(mlib.classify_correction_plan(150.0, 150.0, 0.5),
                         mlib.PLAN_FLY)
        # Floor boundary is EXCLUSIVE-negligible (dv == floor flies).
        self.assertEqual(mlib.classify_correction_plan(0.5, 150.0, 0.5),
                         mlib.PLAN_FLY)

    def test_over_cap(self):
        self.assertEqual(mlib.classify_correction_plan(150.01, 150.0, 0.5),
                         mlib.PLAN_OVER_CAP)
        self.assertEqual(mlib.classify_correction_plan(15_930.0, 150.0, 0.5),
                         mlib.PLAN_OVER_CAP)

    def test_negligible(self):
        self.assertEqual(mlib.classify_correction_plan(0.49, 150.0, 0.5),
                         mlib.PLAN_NEGLIGIBLE)
        self.assertEqual(mlib.classify_correction_plan(0.0, 150.0, 0.5),
                         mlib.PLAN_NEGLIGIBLE)

    def test_nan_dv_fails_closed_to_over_cap(self):
        # An unquantifiable plan never flies; removal is safe (the round
        # falls through and the coast keeps the raw intercept).
        self.assertEqual(mlib.classify_correction_plan(float("nan"), 150.0, 0.5),
                         mlib.PLAN_OVER_CAP)
        self.assertEqual(mlib.classify_correction_plan(float("inf"), 150.0, 0.5),
                         mlib.PLAN_OVER_CAP)

    def test_cap_zero_disables_cap(self):
        self.assertEqual(mlib.classify_correction_plan(9_999.0, 0.0, 0.5),
                         mlib.PLAN_FLY)
        self.assertEqual(mlib.classify_correction_plan(9_999.0, -1.0, 0.5),
                         mlib.PLAN_FLY)


class B5AssertionTests(unittest.TestCase):
    def test_all_met_on_full_flight(self):
        phases = (mlib.B5_PRELAUNCH, mlib.B5_MJ_ASCENT, mlib.B5_CIRCULARIZE,
                  mlib.B5_ORBIT, mlib.B5_PLAN_TRANSFER, mlib.B5_TRANSFER_BURN,
                  mlib.B5_COAST_TO_TARGET, mlib.B5_TARGET_FLYBY, mlib.B5_RETURN)
        outcomes = mlib.evaluate_b5_assertions(
            [], B5_PARAMS, phases_reached=phases, min_target_altitude=61_000.0)
        self.assertEqual([o.name for o in outcomes],
                         ["reachedOrbit", "reachedTargetSoi",
                          "flybyPeriapsisFloor", "returnedToHome"])
        self.assertTrue(mlib.all_assertions_met(outcomes))
        by_name = {o.name: o for o in outcomes}
        self.assertEqual(by_name["reachedTargetSoi"].value, "Mun")
        self.assertEqual(by_name["flybyPeriapsisFloor"].value, 61_000.0)
        self.assertEqual(by_name["returnedToHome"].value, "Kerbin")

    def test_flyby_floor_unmet_below_floor(self):
        phases = (mlib.B5_ORBIT, mlib.B5_TARGET_FLYBY, mlib.B5_RETURN)
        outcomes = mlib.evaluate_b5_assertions(
            [], B5_PARAMS, phases_reached=phases, min_target_altitude=8_000.0)
        by_name = {o.name: o for o in outcomes}
        self.assertFalse(by_name["flybyPeriapsisFloor"].met)

    def test_flyby_floor_unmet_when_never_sampled(self):
        # None (never inside the target SOI, or no finite altitude) is UNMET,
        # never a silent pass; the serialized value scrubs to JSON null.
        outcomes = mlib.evaluate_b5_assertions(
            [], B5_PARAMS, phases_reached=(mlib.B5_ORBIT,), min_target_altitude=None)
        by_name = {o.name: o for o in outcomes}
        self.assertFalse(by_name["flybyPeriapsisFloor"].met)
        self.assertIsNone(by_name["flybyPeriapsisFloor"].to_dict()["value"])

    def test_missing_phases_unmet(self):
        outcomes = mlib.evaluate_b5_assertions(
            [], B5_PARAMS, phases_reached=(mlib.B5_PRELAUNCH, mlib.B5_MJ_ASCENT),
            min_target_altitude=None)
        by_name = {o.name: o for o in outcomes}
        self.assertFalse(by_name["reachedOrbit"].met)
        self.assertFalse(by_name["reachedTargetSoi"].met)
        self.assertFalse(by_name["returnedToHome"].met)


class B5FlameoutStagingTests(unittest.TestCase):
    """Twenty-second live flight (2026-07-22): the Kerbal X core ran dry
    mid-correction (LiquidFuel froze at exactly 720.0 -- the full X200-16
    upper tank behind its decoupler) and both correction rounds burned
    NOTHING against a flamed-out engine. The machine must pop the next stage
    when a COMMANDED burn reads zero available thrust, debounced and bounded,
    and must never pop on a missing reading."""

    def _burning_state(self, **overrides):
        return _b5_state(mlib.B5_CORRECTION_BURN, corr_burn_started=True,
                         min_node_dv=39.0, burn_static_since=100.0,
                         **overrides)

    def _flamed_snap(self, ut, **kw):
        kw.setdefault("body", "Kerbin")
        kw.setdefault("node_count", 1)
        kw.setdefault("node_dv", 39.0)
        kw.setdefault("throttle", 0.25)
        kw.setdefault("available_thrust", 0.0)
        return snap(ut=ut, **kw)

    def test_flameout_stages_after_debounce_and_restamps_progress_anchor(self):
        state = self._burning_state()
        # Frame 1: streak builds, no stage yet (debounce).
        state, actions = mlib.b5_decide(state, self._flamed_snap(200.0))
        self.assertEqual(actions, [])
        self.assertEqual(state.flameout_streak, 1)
        self.assertEqual(state.flameout_stages_done, 0)
        # Frame 2: debounce met -> exactly one ACTIVATE_STAGE; the
        # no-progress anchor re-stamps so the fresh stage earns a full
        # progress window.
        state, actions = mlib.b5_decide(state, self._flamed_snap(201.0))
        self.assertEqual(actions, [Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(state.flameout_stages_done, 1)
        self.assertEqual(state.flameout_streak, 0)
        self.assertEqual(state.burn_static_since, 201.0)
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        self.assertFalse(state.done)

    def test_single_flamed_frame_never_stages(self):
        state = self._burning_state()
        state, actions = mlib.b5_decide(state, self._flamed_snap(200.0))
        self.assertEqual(actions, [])
        # Thrust back (transient reading): streak resets, still no stage.
        state, actions = mlib.b5_decide(state, self._flamed_snap(
            201.0, available_thrust=215_000.0))
        self.assertEqual(actions, [])
        self.assertEqual(state.flameout_streak, 0)
        self.assertEqual(state.flameout_stages_done, 0)

    def test_nan_available_thrust_or_throttle_fails_closed(self):
        state = self._burning_state()
        for _ in range(4):
            state, actions = mlib.b5_decide(state, self._flamed_snap(
                200.0, available_thrust=float("nan")))
            self.assertEqual(actions, [])
        self.assertEqual(state.flameout_stages_done, 0)
        state = self._burning_state()
        for _ in range(4):
            state, actions = mlib.b5_decide(state, self._flamed_snap(
                200.0, throttle=float("nan")))
            self.assertEqual(actions, [])
        self.assertEqual(state.flameout_stages_done, 0)

    def test_zero_throttle_never_stages(self):
        # TRANSFER-BURN autowarp coast: the executor holds throttle 0 between
        # the handoff and the burn -- available_thrust 0 there must not pop.
        state = _b5_state(mlib.B5_TRANSFER_BURN, planned_node_count=1)
        for ut in (10.0, 11.0, 12.0):
            state, actions = mlib.b5_decide(state, snap(
                ut=ut, body="Kerbin", node_count=1, apoapsis=84_000.0,
                periapsis=79_000.0, throttle=0.0, available_thrust=0.0))
            self.assertEqual(actions, [])
        self.assertEqual(state.flameout_stages_done, 0)

    def test_stage_budget_is_bounded(self):
        state = self._burning_state(
            flameout_stages_done=mlib.MAX_FLAMEOUT_STAGES)
        for ut in (200.0, 201.0, 202.0, 203.0):
            state, actions = mlib.b5_decide(state, self._flamed_snap(ut))
            self.assertEqual(actions, [])
        self.assertEqual(state.flameout_stages_done, mlib.MAX_FLAMEOUT_STAGES)
        # The no-progress give-up still owns the outcome past the budget.
        state, _ = mlib.b5_decide(state, self._flamed_snap(
            100.0 + B5_PARAMS.burn_stagnant_seconds))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)

    def test_transfer_burn_flameout_stages_under_executor_throttle(self):
        # The MechJeb executor holds the throttle but never stages: a dry
        # stage mid-TLI pops here too.
        state = _b5_state(mlib.B5_TRANSFER_BURN, planned_node_count=1)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", node_count=1, apoapsis=2_000_000.0,
            periapsis=79_000.0, throttle=1.0, available_thrust=0.0))
        self.assertEqual(actions, [])
        state, actions = mlib.b5_decide(state, snap(
            ut=11.0, body="Kerbin", node_count=1, apoapsis=2_000_000.0,
            periapsis=79_000.0, throttle=1.0, available_thrust=0.0))
        self.assertEqual(actions, [Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(state.flameout_stages_done, 1)

    def test_stale_streak_is_reset_on_burn_entry(self):
        # Delta-review A2: a streak of 1 left behind by a prior burn's exit
        # frame must not weaken the next burn's debounce to a single frame.
        state = _b5_state(mlib.B5_PLAN_CORRECTION, last_plan_ut=0.0,
                          flameout_streak=1)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin",
                                              node_count=1))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        self.assertEqual(state.flameout_streak, 0)

    def test_exit_frame_never_consumes_a_stage_slot(self):
        # Delta-review A1: the pop debounce completing on the same frame the
        # cut threshold is crossed must exit WITHOUT burning a budget slot.
        state = self._burning_state(flameout_streak=1)
        state, actions = mlib.b5_decide(state, self._flamed_snap(
            200.0, node_dv=1.5))  # dv <= cut: exit wins
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.flameout_stages_done, 0)
        self.assertNotIn(Action(mlib.ACTION_ACTIVATE_STAGE), actions)

    def test_flameout_fields_ride_machine_state_and_diff(self):
        before = self._burning_state()
        after = before.__class__(**{**before.__dict__,
                                    "flameout_stages_done": 1})
        line = mlib.format_machine_state(after, 100.0)
        self.assertIn("flameoutStages=1", line)
        changes = mlib.diff_machine_state(before, after)
        self.assertTrue(any("flameoutStages 0->1" in c for c in changes))


class B5ArrivalRecorrectTests(unittest.TestCase):
    """Finding 16 (twenty-third flight): both altitude-triggered rounds
    executed to <1 m/s residual and the arrival was STILL pe -31.8 km. Once
    the altitude rounds are exhausted, a debounced sub-floor PREDICTED
    arrival periapsis (patched-conic next_orbit at the target body) grants a
    bounded extra PLAN-CORRECTION round while enough coast remains; every
    term fails closed."""

    def _coast_state(self, **overrides):
        # Both altitude rounds consumed (B5_PARAMS default triggers = [0, 6M]).
        overrides.setdefault("correction_rounds_done", 2)
        return _b5_state(mlib.B5_COAST_TO_TARGET, **overrides)

    def _bad_snap(self, ut, **kw):
        kw.setdefault("body", "Kerbin")
        kw.setdefault("altitude", 8_000_000.0)
        kw.setdefault("next_body", "Mun")
        kw.setdefault("next_pe", -30_000.0)
        kw.setdefault("time_to_soi", 9_000.0)
        return snap(ut=ut, **kw)

    def test_sustained_sub_floor_arrival_grants_extra_round(self):
        state = self._coast_state()
        for i in range(mlib.ARRIVAL_BAD_DEBOUNCE_FRAMES - 1):
            state, actions = mlib.b5_decide(state, self._bad_snap(10.0 + i))
            self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.arrival_bad_streak,
                         mlib.ARRIVAL_BAD_DEBOUNCE_FRAMES - 1)
        state, actions = mlib.b5_decide(state, self._bad_snap(20.0))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        self.assertEqual(state.extra_rounds_done, 1)
        self.assertEqual(state.arrival_bad_streak, 0)
        self.assertEqual(state.plan_attempts, 1)
        self.assertIn(Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT,
                             B5_PARAMS.course_correct_periapsis,
                             limit=B5_PARAMS.max_correction_dv), actions)

    def test_fail_closed_terms_never_fire(self):
        cases = (
            dict(next_body="Sun"),                    # wrong arrival body
            dict(next_body=""),                       # no reading
            dict(next_pe=float("nan")),               # unreadable pe
            dict(next_pe=61_000.0),                   # healthy arrival
            dict(time_to_soi=float("nan")),           # unknown crossing
            dict(time_to_soi=300.0),                  # too close to fly it
            dict(node_count=1, node_ut=100_000.0,
                 next_pe=-30_000.0),                  # a node is pending
        )
        for kw in cases:
            state = self._coast_state()
            for i in range(mlib.ARRIVAL_BAD_DEBOUNCE_FRAMES + 2):
                state, _ = mlib.b5_decide(state, self._bad_snap(10.0 + i, **kw))
            self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET,
                             "gate fired for %r" % (kw,))
            self.assertEqual(state.extra_rounds_done, 0)

    def test_never_fires_while_altitude_rounds_pending(self):
        # Below the round-2 trigger with round 1 consumed: the altitude
        # machinery owns the next round; the arrival gate must stay quiet.
        state = self._coast_state(correction_rounds_done=1)
        for i in range(mlib.ARRIVAL_BAD_DEBOUNCE_FRAMES + 2):
            state, _ = mlib.b5_decide(state, self._bad_snap(
                10.0 + i, altitude=2_000_000.0))
        self.assertEqual(state.extra_rounds_done, 0)
        self.assertEqual(state.arrival_bad_streak, 0)

    def test_extra_rounds_are_bounded(self):
        state = self._coast_state(
            extra_rounds_done=mlib.MAX_ARRIVAL_EXTRA_ROUNDS)
        for i in range(mlib.ARRIVAL_BAD_DEBOUNCE_FRAMES + 2):
            state, _ = mlib.b5_decide(state, self._bad_snap(10.0 + i))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.extra_rounds_done, mlib.MAX_ARRIVAL_EXTRA_ROUNDS)

    def test_healthy_frame_resets_the_streak(self):
        state = self._coast_state()
        state, _ = mlib.b5_decide(state, self._bad_snap(10.0))
        state, _ = mlib.b5_decide(state, self._bad_snap(11.0))
        self.assertEqual(state.arrival_bad_streak, 2)
        state, _ = mlib.b5_decide(state, self._bad_snap(12.0, next_pe=61_000.0))
        self.assertEqual(state.arrival_bad_streak, 0)
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)

    def test_granted_round_flows_into_the_burn_machinery(self):
        # The extra round is a REAL round: node arrives -> CORRECTION-BURN
        # via the standard plan handoff, and its exit bumps rounds to 3.
        state = self._coast_state()
        for i in range(mlib.ARRIVAL_BAD_DEBOUNCE_FRAMES):
            state, _ = mlib.b5_decide(state, self._bad_snap(10.0 + i))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        state, actions = mlib.b5_decide(state, self._bad_snap(
            20.0, node_count=1, node_dv=12.0))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        self.assertIn(Action(mlib.ACTION_AP_POINT_NODE), actions)


class B5ImpactTerminalTests(unittest.TestCase):
    """Twenty-second live flight: an under-corrected arrival put the flyby on
    a sub-surface periapsis and the mission rode the descent 589 wall-seconds
    at 1x to physical destruction (the certification audit's only 1x-coast
    violation). Sustained impact-bound frames now terminate ASSERT-FAIL
    early; a short transient still only costs 1x polls."""

    def _impact_snap(self, ut, **kw):
        kw.setdefault("body", "Mun")
        kw.setdefault("altitude", 300_000.0)
        kw.setdefault("periapsis", -28_000.0)
        return snap(ut=ut, **kw)

    def test_sustained_impact_bound_terminates_assert_fail(self):
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        for i in range(mlib.IMPACT_TERMINAL_DEBOUNCE_FRAMES - 1):
            state, _ = mlib.b5_decide(state, self._impact_snap(10.0 + i))
            self.assertFalse(state.done)
        self.assertEqual(state.impact_certain_streak,
                         mlib.IMPACT_TERMINAL_DEBOUNCE_FRAMES - 1)
        state, actions = mlib.b5_decide(state, self._impact_snap(20.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("impact certain", state.loss_reason)
        self.assertIn("early terminal", state.loss_reason)
        # No warp was held (the guard zeroed it on frame 1): silent exit.
        self.assertEqual(actions, [])

    def test_terminal_cancels_active_native_warp(self):
        state = _b5_state(mlib.B5_TARGET_FLYBY,
                          impact_certain_streak=mlib.IMPACT_TERMINAL_DEBOUNCE_FRAMES - 1,
                          warp_to_cmd=99_999.0, last_warp_issue_ut=5.0)
        state, actions = mlib.b5_decide(state, self._impact_snap(
            20.0, warping_to=99_999.0, warp_mode="RAILS", warp_rate=1000.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIsNone(state.warp_to_cmd)
        self.assertEqual(actions, [Action(mlib.ACTION_CANCEL_WARP)])

    def test_transient_impact_reading_resets_the_streak(self):
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        state, _ = mlib.b5_decide(state, self._impact_snap(10.0))
        state, _ = mlib.b5_decide(state, self._impact_snap(11.0))
        self.assertEqual(state.impact_certain_streak, 2)
        # One healthy frame (positive periapsis) resets the countdown.
        state, _ = mlib.b5_decide(state, self._impact_snap(
            12.0, periapsis=61_000.0))
        self.assertEqual(state.impact_certain_streak, 0)
        self.assertFalse(state.done)
        # Streak re-earns from zero.
        state, _ = mlib.b5_decide(state, self._impact_snap(13.0))
        self.assertEqual(state.impact_certain_streak, 1)
        self.assertFalse(state.done)

    def test_high_altitude_impact_periapsis_never_counts(self):
        # Above the guard altitude the outcome is NOT decided (corrections /
        # SOI exit may still change it): no streak, no terminal.
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        for i in range(mlib.IMPACT_TERMINAL_DEBOUNCE_FRAMES + 2):
            state, _ = mlib.b5_decide(state, self._impact_snap(
                10.0 + i, altitude=800_000.0))
            self.assertFalse(state.done)
        self.assertEqual(state.impact_certain_streak, 0)


if __name__ == "__main__":
    unittest.main()
