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
# EVA-4 phase state machine (mission eva4_atmo_chute).
# ---------------------------------------------------------------------------


EVA4_PARAMS = mlib.Eva4Params(
    throttle=1.0,
    craft_chute_arm_max_rate=30.0,
    craft_chute_full_deploy_alt=2500.0,
    eva_window_max_alt=2100.0,
    eva_window_min_alt=700.0,
    eva_max_descent_rate=25.0,
    ascent_timeout=90.0,
    coast_timeout=180.0,
    descent_timeout=240.0,
    apoapsis_window=(6000.0, 30000.0),
)

# The observed full-canopy state the window gates on (kRPC ParachuteState, normalized).
_CHUTE_FULL = mlib.CHUTE_STATE_DEPLOYED
_CHUTE_ARMED = mlib.CHUTE_STATE_ARMED


def drive_eva4(state, frames):
    """Feed a list of snapshots through eva4_decide, returning
    (final_state, [actions_per_frame])."""
    per_frame = []
    for f in frames:
        state, actions = mlib.eva4_decide(state, f)
        per_frame.append(actions)
    return state, per_frame


def _eva4_descent_state(params=EVA4_PARAMS, **overrides):
    """An Eva4State pinned in DESCENT with the craft chute already COMMANDED (armed)."""
    base = mlib.eva4_initial_state(params)
    fields = {**base.__dict__, "phase": mlib.EVA4_DESCENT, "phase_entry_ut": 0.0,
              "chute_armed": True}
    fields.update(overrides)
    return base.__class__(**fields)


class Eva4WindowGateTests(unittest.TestCase):
    """Guards ``eva4_window_open`` -- the single decision the whole mission exists to
    make. The seam's IRREVERSIBLE EvaExit fires on its say-so, so every conjunct must
    fail CLOSED: a window that opens one frame too early puts a kerbal out of the hatch
    at terminal velocity, and one that opens too low leaves it no sky for its canopy.

    The first conjunct is the FLIGHT-1 REGRESSION GUARD: it reads the craft's OBSERVED
    parachute state, never the machine's own "we commanded it" latch. Flight 1 armed the
    chute at 2382 m / -301 m/s, the canopy never opened (stock refuses ACTIVE ->
    SEMIDEPLOYED while automateSafeDeploy = 0 and DeploySafe reads unsafe at that speed),
    and the commanded latch was true the whole time."""

    def test_open_when_every_conjunct_holds(self):
        self.assertTrue(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=1800.0, vertical_speed=-9.0, situation="FLYING",
                 craft_chute_state=_CHUTE_FULL)))

    def test_shut_when_chute_only_armed(self):
        # THE flight-1 regression cell: commanded but never opened must NEVER open the
        # window, no matter how good every other conjunct looks.
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=1800.0, vertical_speed=-9.0, situation="FLYING",
                 craft_chute_state=_CHUTE_ARMED)))

    def test_shut_when_chute_only_semi_deployed(self):
        # A streamer is not a full canopy: the craft is still fast.
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=1800.0, vertical_speed=-9.0, situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED)))

    def test_shut_on_unread_chute_state(self):
        # "" is the unread sentinel (a runner that did not opt into read_chute, or a
        # faulted read): fail CLOSED, never EVA on a blind frame.
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=1800.0, vertical_speed=-9.0, situation="FLYING")))

    def test_shut_above_ceiling(self):
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=2100.1, vertical_speed=-9.0, situation="FLYING",
                 craft_chute_state=_CHUTE_FULL)))

    def test_shut_below_floor(self):
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=699.9, vertical_speed=-9.0, situation="FLYING",
                 craft_chute_state=_CHUTE_FULL)))

    def test_shut_while_falling_too_fast(self):
        # Cross-check that the observed canopy is actually doing work.
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=1800.0, vertical_speed=-95.0, situation="FLYING",
                 craft_chute_state=_CHUTE_FULL)))

    def test_open_at_inclusive_bounds(self):
        # The bounds are inclusive on all three axes.
        self.assertTrue(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=2100.0, vertical_speed=-25.0, situation="FLYING",
                 craft_chute_state=_CHUTE_FULL)))
        self.assertTrue(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=700.0, vertical_speed=-25.0, situation="FLYING",
                 craft_chute_state=_CHUTE_FULL)))

    def test_shut_when_already_landed(self):
        # A landed craft is the EVA-1 ground case, not this scenario's surface.
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=1000.0, vertical_speed=0.0, situation="LANDED",
                 craft_chute_state=_CHUTE_FULL)))

    def test_shut_on_unreadable_telemetry(self):
        # NaN altitude / vertical speed fail CLOSED.
        nan = float("nan")
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=nan, vertical_speed=-9.0, situation="FLYING",
                 craft_chute_state=_CHUTE_FULL)))
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=1800.0, vertical_speed=nan, situation="FLYING",
                 craft_chute_state=_CHUTE_FULL)))

    def test_shut_on_blank_situation(self):
        self.assertFalse(mlib.eva4_window_open(
            EVA4_PARAMS,
            snap(altitude=1800.0, vertical_speed=-9.0,
                 craft_chute_state=_CHUTE_FULL)))


class Eva4ParachuteStateNormalizationTests(unittest.TestCase):
    """The window compares against PascalCase constants, so the kRPC lower_snake
    enum names must normalize onto them exactly -- a mismatch would silently shut the
    window forever."""

    def test_krpc_names_normalize_onto_the_gate_constants(self):
        self.assertEqual(mlib.normalize_parachute_state("stowed"),
                         mlib.CHUTE_STATE_STOWED)
        self.assertEqual(mlib.normalize_parachute_state("armed"),
                         mlib.CHUTE_STATE_ARMED)
        self.assertEqual(mlib.normalize_parachute_state("semi_deployed"),
                         mlib.CHUTE_STATE_SEMI_DEPLOYED)
        self.assertEqual(mlib.normalize_parachute_state("deployed"),
                         mlib.CHUTE_STATE_DEPLOYED)
        self.assertEqual(mlib.normalize_parachute_state("cut"),
                         mlib.CHUTE_STATE_CUT)

    def test_empty_and_none_fail_closed(self):
        self.assertEqual(mlib.normalize_parachute_state(""), "")
        self.assertEqual(mlib.normalize_parachute_state(None), "")


class Eva4MachineTests(unittest.TestCase):
    """Guards the EVA-4 machine: the B1 hop shape up to DESCENT, but terminating
    AIRBORNE at EVA-WINDOW so the seam can EVA. A mis-wired terminal here either hands
    the seam a landed craft (wrong scenario entirely) or burns the descent budget."""

    def test_prelaunch_emits_throttle_and_stage_then_ascent(self):
        state = mlib.eva4_initial_state(EVA4_PARAMS)
        new, actions = mlib.eva4_decide(state, snap(ut=0.0))
        self.assertEqual(new.phase, mlib.EVA4_ASCENT)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_THROTTLE, 1.0),
                                   Action(mlib.ACTION_ACTIVATE_STAGE)])

    def test_full_happy_path_ends_airborne_under_observed_canopy(self):
        # The whole PRELAUNCH -> EVA-WINDOW walk, shaped by the flight-1 measurements:
        # the craft is armed at the APOAPSIS CROSSING (~11.9 km, -7.4 m/s - the measured
        # first DESCENT frame), the canopy is observed open on the way down, and the
        # window opens once the craft has been inside the band under a FULL canopy for
        # EVA4_WINDOW_DEBOUNCE_K consecutive frames (one open frame is NOT enough).
        state = mlib.eva4_initial_state(EVA4_PARAMS)
        frames = [
            snap(ut=0.0),                                            # PRELAUNCH->ASCENT
            snap(ut=2.0, stage_solid_fuel=10.0, apoapsis=5000.0),    # ASCENT
            snap(ut=19.9, stage_solid_fuel=0.0, apoapsis=19879.0),   # ASCENT->COAST
            snap(ut=40.0, vertical_speed=200.0, apoapsis=19879.0),   # COAST (rising)
            snap(ut=60.6, vertical_speed=2.2, apoapsis=19879.0,
                 altitude=11965.0),                                  # COAST (apoapsis)
            snap(ut=61.6, vertical_speed=-7.4, altitude=11961.0,
                 situation="FLYING", craft_chute_state=mlib.CHUTE_STATE_STOWED),
            # ^ COAST->DESCENT transition frame: the machine falls through into the
            #   DESCENT body on this SAME frame and ARMS here (|vs| 7.4 <= 30), at the
            #   apoapsis crossing, exactly where DeploySafe is trivially SAFE.
            snap(ut=70.0, vertical_speed=-60.0, altitude=11500.0,
                 situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),  # streamer, too fast
            snap(ut=200.0, vertical_speed=-40.0, altitude=2400.0,
                 situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),  # above ceiling
            snap(ut=229.5, vertical_speed=-9.5, altitude=1910.0,
                 situation="FLYING", craft_chute_state=_CHUTE_FULL),  # streak 1 of K
            snap(ut=230.0, vertical_speed=-9.0, altitude=1900.0,
                 situation="FLYING", craft_chute_state=_CHUTE_FULL),  # streak K: OPENS
        ]
        state, per_frame = drive_eva4(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.EVA4_EVA_WINDOW)
        self.assertIsNone(state.verdict)
        self.assertIsNone(state.loss_reason)
        self.assertEqual(state.peak_apoapsis, 19879.0)
        self.assertEqual(state.eva_window_altitude, 1900.0)
        self.assertEqual(state.eva_window_vertical_speed, -9.0)
        self.assertTrue(state.craft_chute_full_seen)
        # The terminal hands a still-descending craft to the seam: skip the settle tail.
        self.assertTrue(state.skip_settle_tail)
        # The arm fires ONCE, at DESCENT entry, and carries its evidence.
        arm_frames = [i for i, acts in enumerate(per_frame)
                      if Action(mlib.ACTION_DEPLOY_CHUTE) in acts]
        self.assertEqual(arm_frames, [5])
        self.assertEqual(state.phases_reached.count(mlib.EVA4_DESCENT), 1)
        self.assertEqual(
            per_frame[5],
            [Action(mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE, 2500.0),
             Action(mlib.ACTION_DEPLOY_CHUTE)])
        self.assertEqual(state.chute_armed_altitude, 11961.0)
        self.assertEqual(state.chute_armed_rate, -7.4)
        self.assertEqual(state.phases_reached,
                         (mlib.EVA4_PRELAUNCH, mlib.EVA4_ASCENT, mlib.EVA4_COAST,
                          mlib.EVA4_DESCENT, mlib.EVA4_EVA_WINDOW))

    def test_coast_to_descent_arms_on_the_transition_frame(self):
        # Regression guard on the one-poll delay: the COAST -> DESCENT transition frame
        # must RUN the descent body, so the arm lands at the apoapsis crossing (-7.4 m/s
        # measured) rather than a poll later (-16.9 m/s) and, worse, drifting outside the
        # bound entirely on a coarse poll.
        state = mlib.eva4_initial_state(EVA4_PARAMS)
        state = state.__class__(**{**state.__dict__, "phase": mlib.EVA4_COAST,
                                   "phase_entry_ut": 0.0})
        state, actions = mlib.eva4_decide(
            state, snap(ut=61.6, altitude=11961.0, vertical_speed=-7.4,
                        situation="FLYING"))
        self.assertEqual(state.phase, mlib.EVA4_DESCENT)
        self.assertTrue(state.chute_armed)
        self.assertEqual([a.kind for a in actions],
                         [mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE,
                          mlib.ACTION_DEPLOY_CHUTE])
        self.assertEqual(state.chute_armed_rate, -7.4)

    def test_arm_waits_until_inside_the_rate_bound(self):
        # Regression on the flight-1 root cause from the other side: if the machine ever
        # finds itself in DESCENT already fast, it must NOT arm (an arm at 300 m/s is
        # inert), and it must keep waiting rather than pretending it armed.
        state = mlib.eva4_initial_state(EVA4_PARAMS)
        state = state.__class__(**{**state.__dict__, "phase": mlib.EVA4_DESCENT,
                                   "phase_entry_ut": 0.0})
        state, actions = mlib.eva4_decide(
            state, snap(ut=1.0, altitude=5000.0, vertical_speed=-301.0,
                        situation="FLYING"))
        self.assertEqual(actions, [])
        self.assertFalse(state.chute_armed)

    def test_arm_sets_deploy_altitude_before_arming(self):
        # Order matters for the module's first ACTIVE FixedUpdate: the raised altitude
        # must be in place on the same frame the chute is armed.
        state = mlib.eva4_initial_state(EVA4_PARAMS)
        state = state.__class__(**{**state.__dict__, "phase": mlib.EVA4_DESCENT,
                                   "phase_entry_ut": 0.0})
        _, actions = mlib.eva4_decide(
            state, snap(ut=1.0, altitude=11961.0, vertical_speed=-7.4,
                        situation="FLYING"))
        self.assertEqual([a.kind for a in actions],
                         [mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE,
                          mlib.ACTION_DEPLOY_CHUTE])
        self.assertEqual(actions[0].value, 2500.0)

    def test_arm_fires_exactly_once(self):
        state = _eva4_descent_state()
        _, actions = mlib.eva4_decide(
            state, snap(ut=5.0, altitude=8000.0, vertical_speed=-5.0,
                        situation="FLYING"))
        self.assertEqual(actions, [])

    def test_window_missed_below_floor_names_the_observed_chute_state(self):
        # THE flight-1 failure mode, now self-naming: the chute was commanded but only
        # ever read Armed, so the reason must say craftChute=Armed (not "armed" from the
        # machine's own latch).
        state = _eva4_descent_state()
        state, _ = mlib.eva4_decide(
            state, snap(ut=10.0, altitude=600.0, vertical_speed=-295.0,
                        situation="FLYING", craft_chute_state=_CHUTE_ARMED))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("eva-window-missed", state.loss_reason)
        self.assertIn("craftChute=Armed", state.loss_reason)
        self.assertIn("armCommanded=yes", state.loss_reason)
        self.assertIn("700", state.loss_reason)
        self.assertNotEqual(state.phase, mlib.EVA4_EVA_WINDOW)

    def test_window_missed_names_an_unread_chute(self):
        state = _eva4_descent_state()
        state, _ = mlib.eva4_decide(
            state, snap(ut=10.0, altitude=600.0, vertical_speed=-20.0,
                        situation="FLYING"))
        self.assertIn("craftChute=UNREAD", state.loss_reason)

    def test_window_wins_over_floor_on_the_same_frame(self):
        # A frame that is inside the window is never also "below the floor": the
        # window check runs FIRST so an exactly-at-floor frame succeeds. With the
        # debounce that takes K such frames; the first one must NOT trip the floor
        # either (700 is inside, not below), so the run survives to the second.
        state = _eva4_descent_state()
        for _ in range(mlib.EVA4_WINDOW_DEBOUNCE_K):
            state, _ = mlib.eva4_decide(
                state, snap(ut=10.0, altitude=700.0, vertical_speed=-8.0,
                            situation="FLYING", craft_chute_state=_CHUTE_FULL))
        self.assertEqual(state.phase, mlib.EVA4_EVA_WINDOW)
        self.assertIsNone(state.verdict)

    def test_window_needs_k_consecutive_open_frames(self):
        # A SINGLE open frame must never hand a kerbal out of a hatch: stock flips
        # ParachuteState to DEPLOYED at the START of the ~8 s canopy animation, so one
        # glitched / stale kRPC frame could otherwise certify a terminal-velocity EVA.
        state = _eva4_descent_state()
        good = dict(altitude=1800.0, vertical_speed=-9.0, situation="FLYING",
                    craft_chute_state=_CHUTE_FULL)
        state, _ = mlib.eva4_decide(state, snap(ut=10.0, **good))
        self.assertEqual(state.phase, mlib.EVA4_DESCENT)
        self.assertEqual(state.window_open_streak, 1)
        state, _ = mlib.eva4_decide(state, snap(ut=11.0, **good))
        self.assertEqual(state.phase, mlib.EVA4_EVA_WINDOW)
        self.assertEqual(state.eva_window_altitude, 1800.0)

    def test_window_streak_resets_on_a_disagreeing_frame(self):
        # Fail-closed: the run of agreement must be UNBROKEN. One frame that reads the
        # chute merely Armed (or is otherwise outside the envelope) sends the streak back
        # to 0, so the handoff needs K fresh agreeing frames again.
        state = _eva4_descent_state()
        good = dict(altitude=1800.0, vertical_speed=-9.0, situation="FLYING",
                    craft_chute_state=_CHUTE_FULL)
        state, _ = mlib.eva4_decide(state, snap(ut=10.0, **good))
        self.assertEqual(state.window_open_streak, 1)
        state, _ = mlib.eva4_decide(
            state, snap(ut=11.0, altitude=1800.0, vertical_speed=-9.0,
                        situation="FLYING", craft_chute_state=_CHUTE_ARMED))
        self.assertEqual(state.window_open_streak, 0)
        self.assertEqual(state.phase, mlib.EVA4_DESCENT)
        state, _ = mlib.eva4_decide(state, snap(ut=12.0, **good))
        self.assertEqual(state.phase, mlib.EVA4_DESCENT)
        state, _ = mlib.eva4_decide(state, snap(ut=13.0, **good))
        self.assertEqual(state.phase, mlib.EVA4_EVA_WINDOW)

    def test_window_missed_still_reds_when_the_craft_sinks_mid_streak(self):
        # The debounce must not convert a real miss into a late handoff: a craft that
        # drops below the floor while the streak is still short reds by NAME (the floor
        # check is unchanged and runs on the same frame the streak resets).
        state = _eva4_descent_state()
        state, _ = mlib.eva4_decide(
            state, snap(ut=10.0, altitude=705.0, vertical_speed=-9.0,
                        situation="FLYING", craft_chute_state=_CHUTE_FULL))
        self.assertEqual(state.window_open_streak, 1)
        self.assertEqual(state.phase, mlib.EVA4_DESCENT)
        state, _ = mlib.eva4_decide(
            state, snap(ut=11.0, altitude=690.0, vertical_speed=-9.0,
                        situation="FLYING", craft_chute_state=_CHUTE_FULL))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("eva-window-missed", state.loss_reason)

    def test_vessel_lost_is_assert_fail_not_a_down_success(self):
        # Unlike B1 there is NO chute-deployed-impact success terminal: the craft
        # reaching the ground at all means the EVA never happened.
        state = _eva4_descent_state()
        state, _ = mlib.eva4_decide(state, snap(ut=10.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", state.loss_reason)

    def test_descent_budget_overrun_flakes_naming_phase(self):
        state = _eva4_descent_state()
        state, _ = mlib.eva4_decide(
            state, snap(ut=900.0, altitude=1500.0, vertical_speed=-95.0,
                        situation="FLYING"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.EVA4_DESCENT)

    def test_frozen_telemetry_trips_assert_fail(self):
        params = EVA4_PARAMS.__class__(**{**EVA4_PARAMS.__dict__,
                                          "frozen_sample_limit": 3})
        state = _eva4_descent_state(params)
        for f in _frozen_frames(4, altitude=1500.0, vertical_speed=-95.0):
            state, _ = mlib.eva4_decide(state, f)
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("frozen", state.loss_reason)

    def test_done_state_is_idempotent(self):
        state = mlib.eva4_initial_state(EVA4_PARAMS)
        term = state.__class__(**{**state.__dict__, "phase": mlib.EVA4_EVA_WINDOW,
                                  "done": True})
        new, actions = mlib.eva4_decide(term, snap(ut=999.0))
        self.assertIs(new, term)
        self.assertEqual(actions, [])

    def test_nan_ut_does_not_trip_budget(self):
        state = mlib.eva4_initial_state(EVA4_PARAMS)
        state, _ = mlib.eva4_decide(state, snap(ut=0.0))
        state, _ = mlib.eva4_decide(state, snap(ut=float("nan"),
                                                stage_solid_fuel=50.0))
        self.assertEqual(state.phase, mlib.EVA4_ASCENT)
        self.assertIsNone(state.verdict)


class Eva4ParamTests(unittest.TestCase):
    """Guards eva4_params_from_dict: the spec's missionParams are the ONLY source of
    the arming rule and the EVA envelope, so a dropped / mistyped key must not silently
    widen them."""

    def test_reads_every_declared_key(self):
        p = mlib.eva4_params_from_dict({
            "throttle": 1.0,
            "apoapsisWindowMeters": {"min": 6000, "max": 30000},
            "craftChuteArmMaxRateMps": 30,
            "craftChuteFullDeployAltMeters": 2500,
            "evaWindowMaxAltMeters": 2100,
            "evaWindowMinAltMeters": 700,
            "evaMaxDescentRateMps": 25,
            "ascentTimeoutSeconds": 90,
            "coastTimeoutSeconds": 180,
            "descentTimeoutSeconds": 480,
            "frozenTelemetrySamples": 7,
            "airborneSituations": ["FLYING"],
        })
        self.assertEqual(p.apoapsis_window, (6000.0, 30000.0))
        self.assertEqual(p.craft_chute_arm_max_rate, 30.0)
        self.assertEqual(p.craft_chute_full_deploy_alt, 2500.0)
        self.assertEqual(p.eva_window_max_alt, 2100.0)
        self.assertEqual(p.eva_window_min_alt, 700.0)
        self.assertEqual(p.eva_max_descent_rate, 25.0)
        self.assertEqual(p.descent_timeout, 480.0)
        self.assertEqual(p.frozen_sample_limit, 7)
        self.assertEqual(p.airborne_situations, ("FLYING",))

    def test_defaults_are_conservative(self):
        p = mlib.eva4_params_from_dict({})
        self.assertEqual(p.frozen_sample_limit, 10)
        self.assertEqual(p.airborne_situations, ("FLYING", "SUB_ORBITAL"))


class Eva4AssertionTests(unittest.TestCase):
    """Guards the EVA-4 assertion evaluator: it re-states the handoff contract in the
    RESULT JSON, so a future window re-tune cannot move the exit envelope invisibly."""

    def _terminal_state(self, alt=1900.0, vs=-9.0, full_seen=True):
        base = mlib.eva4_initial_state(EVA4_PARAMS)
        return base.__class__(**{**base.__dict__, "phase": mlib.EVA4_EVA_WINDOW,
                                 "done": True, "eva_window_altitude": alt,
                                 "eva_window_vertical_speed": vs,
                                 "chute_armed": True,
                                 "chute_armed_altitude": 11961.0,
                                 "chute_armed_rate": -7.4,
                                 "craft_chute_full_seen": full_seen})

    def test_all_four_met_on_a_good_handoff(self):
        frames = [snap(apoapsis=19879.0), snap(apoapsis=8000.0)]
        outs = mlib.evaluate_eva4_assertions(frames, EVA4_PARAMS,
                                             self._terminal_state())
        by_name = {o.name: o for o in outs}
        self.assertEqual(set(by_name), {"apoapsisWindow", "evaWindowReached",
                                        "evaWindowDescentRate", "craftCanopyObserved"})
        self.assertTrue(all(o.met for o in outs))
        self.assertEqual(by_name["evaWindowReached"].value, 1900.0)
        self.assertEqual(by_name["evaWindowDescentRate"].value, -9.0)
        # The canopy row carries the COMMANDED altitude/rate alongside the OBSERVED bit,
        # so the result JSON shows both halves of the flight-1 distinction.
        self.assertEqual(by_name["craftCanopyObserved"].value, 11961.0)
        self.assertEqual(by_name["craftCanopyObserved"].detail["armCommandedRate"], -7.4)

    def test_commanded_but_never_observed_canopy_is_unmet(self):
        # The flight-1 shape: armed, believed, never opened.
        outs = mlib.evaluate_eva4_assertions([snap(apoapsis=19879.0)], EVA4_PARAMS,
                                             self._terminal_state(full_seen=False))
        by_name = {o.name: o for o in outs}
        self.assertFalse(by_name["craftCanopyObserved"].met)
        self.assertTrue(by_name["craftCanopyObserved"].detail["armCommanded"])

    def test_window_not_reached_is_unmet(self):
        base = mlib.eva4_initial_state(EVA4_PARAMS)
        stuck = base.__class__(**{**base.__dict__, "phase": mlib.EVA4_DESCENT})
        outs = mlib.evaluate_eva4_assertions([snap(apoapsis=19879.0)], EVA4_PARAMS,
                                             stuck)
        by_name = {o.name: o for o in outs}
        self.assertFalse(by_name["evaWindowReached"].met)
        self.assertFalse(by_name["evaWindowDescentRate"].met)
        self.assertEqual(by_name["evaWindowReached"].detail["terminalPhase"],
                         mlib.EVA4_DESCENT)

    def test_out_of_window_apoapsis_is_unmet(self):
        outs = mlib.evaluate_eva4_assertions([snap(apoapsis=45000.0)], EVA4_PARAMS,
                                             self._terminal_state())
        by_name = {o.name: o for o in outs}
        self.assertFalse(by_name["apoapsisWindow"].met)
        self.assertEqual(by_name["apoapsisWindow"].value, 45000.0)

    def test_too_fast_handoff_fails_the_rate_assertion(self):
        outs = mlib.evaluate_eva4_assertions([snap(apoapsis=19879.0)], EVA4_PARAMS,
                                             self._terminal_state(vs=-95.0))
        by_name = {o.name: o for o in outs}
        self.assertFalse(by_name["evaWindowDescentRate"].met)

    def test_rows_serialize_json_safe(self):
        outs = mlib.evaluate_eva4_assertions([], EVA4_PARAMS, None)
        for o in outs:
            row = o.to_dict()
            self.assertIn("name", row)
            self.assertIn("met", row)
            # NaN evidence is scrubbed to JSON null.
            self.assertTrue(row["value"] is None
                            or isinstance(row["value"], (float, int, str)))


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

    def test_rails_decay_physics_label_not_unexpected_when_rails_allowed(self):
        # B7 review MAJOR-1 (the finding-19b insight applied to this guard):
        # commanding a physics flip mid-rails-ramp flips TimeWarp.Mode to LOW
        # immediately while CurrentRate still decays from 100,000x -- kRPC
        # truthfully reports PHYSICS at 4.4-5.3x (flight 7 logged SIX
        # near-flake strikes). Stock physics warp cannot exceed 4x, so a
        # rails-allowed mission treats the over-ceiling PHYSICS label as the
        # rails-decay artifact it is.
        for rate in (4.36, 4.76, 5.32, 48_000.0):
            self.assertFalse(mlib.is_unexpected_warp(
                mlib.WARP_PHYSICS, rate, True, max_physics_warp=4.0))
            self.assertFalse(mlib.is_unexpected_warp(
                mlib.WARP_PHYSICS, rate, True))  # even with no physics grant
        # A rails-FORBIDDEN mission (B1) still flakes the artifact: rails
        # decay implies rails ran, which its contract forbids outright.
        self.assertTrue(mlib.is_unexpected_warp(
            mlib.WARP_PHYSICS, 5.32, False, max_physics_warp=4.0))
        # In-ceiling PHYSICS rates are still judged by the physics bound
        # alone (a genuine 4x flip under a 2.0 grant remains a violation).
        self.assertTrue(mlib.is_unexpected_warp(
            mlib.WARP_PHYSICS, 3.9, True, max_physics_warp=2.0))

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

    def test_transfer_burn_flameout_with_collapsed_throttle_stages(self):
        # Finding 17 (B7 third flight): the MechJeb executor COLLAPSES the
        # throttle to zero when the engine dies mid-burn (ejection flamed
        # out at 476.9 of 797.6 m/s remaining, thr readback 0.000), so the
        # commanded-throttle evidence is blind under it. A burn that
        # demonstrably ran (orbit changed since entry) with the node still
        # pending + zero available thrust stages anyway.
        state = _b5_state(mlib.B5_TRANSFER_BURN, planned_node_count=1,
                          burn_entry_ap=778_000.0, burn_entry_pe=560_000.0)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", node_count=1, node_dv=476.9,
            apoapsis=2_541_000.0, periapsis=562_000.0,
            throttle=0.0, available_thrust=0.0))
        self.assertEqual(actions, [])
        state, actions = mlib.b5_decide(state, snap(
            ut=11.0, body="Kerbin", node_count=1, node_dv=476.9,
            apoapsis=2_541_000.0, periapsis=562_000.0,
            throttle=0.0, available_thrust=0.0))
        self.assertEqual(actions, [Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(state.flameout_stages_done, 1)

    def test_transfer_burn_preburn_zero_thrust_never_stages(self):
        # Pre-burn (orbit UNCHANGED since entry): zero available thrust with
        # a collapsed throttle must not pop -- no burn evidence exists.
        state = _b5_state(mlib.B5_TRANSFER_BURN, planned_node_count=1,
                          burn_entry_ap=778_000.0, burn_entry_pe=560_000.0)
        for ut in (10.0, 11.0, 12.0, 13.0):
            state, actions = mlib.b5_decide(state, snap(
                ut=ut, body="Kerbin", node_count=1, node_dv=797.6,
                apoapsis=778_000.0, periapsis=560_000.0,
                throttle=0.0, available_thrust=0.0))
            self.assertEqual(actions, [])
        self.assertEqual(state.flameout_stages_done, 0)

    def test_transfer_burn_consumed_node_never_stages_mid_burn_path(self):
        # Node consumed (count fell below the handoff): the mid-burn
        # evidence is gone; a zero-thrust frame must not pop.
        state = _b5_state(mlib.B5_TRANSFER_BURN, planned_node_count=1,
                          burn_entry_ap=778_000.0, burn_entry_pe=560_000.0)
        for ut in (10.0, 11.0, 12.0):
            state, actions = mlib.b5_decide(state, snap(
                ut=ut, body="Kerbin", node_count=0,
                apoapsis=2_541_000.0, periapsis=562_000.0,
                throttle=0.0, available_thrust=0.0))
            self.assertNotIn(Action(mlib.ACTION_ACTIVATE_STAGE), actions)
        self.assertEqual(state.flameout_stages_done, 0)

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
        # Inside the high-precision window (twenty-fourth flight: far-out
        # extras moved the arrival only ~2-4 km each and were wasted).
        kw.setdefault("time_to_soi", 3_000.0)
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
            dict(time_to_soi=9_000.0),                # too FAR: hold for the
                                                      # high-precision window
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


# B7 interplanetary fixture (design docs/dev/design-autotest-b7-duna.md 5.8):
# the shared B5 machine with the five B7 params ON, the 700 km park, the Duna
# target, and the spec-shaped budgets (the ejection-window autowarp spans up
# to ~1 synodic ~= 20M game s; the heliocentric coast ~7M game s).
B7_PARAMS = mlib.B5Params(**{
    **B5_PARAMS.__dict__,
    "target_apoapsis": 700_000.0,
    "target_periapsis": 700_000.0,
    "apo_error": 15_000.0,
    "peri_error": 15_000.0,
    "target_body": "Duna",
    "transfer_min_apoapsis": 0.0,
    "course_correct_periapsis": 50_000.0,
    "max_correction_dv": 200.0,
    "correction_trigger_alts": (),
    "correction_trigger_time_to_soi": (20_000_000.0, 500_000.0),
    "via_bodies": ("Sun",),
    "return_body": "Sun",
    "interplanetary_transfer": True,
    "ejection_ecc_floor": 1.05,
    "transfer_burn_timeout": 25_000_000.0,
    "coast_timeout": 12_000_000.0,
    "flyby_timeout": 500_000.0,
    "coast_warp_factor": 7,
    "target_periapsis_floor": 15_000.0,
})


def _b7_state(phase, **overrides):
    """A B5State carrying the B7 params, pinned in ``phase`` with
    phase_entry_ut=0 (mirror of ``_b5_state``)."""
    base = mlib.b5_initial_state(B7_PARAMS)
    fields = {**base.__dict__, "phase": phase, "phase_entry_ut": 0.0}
    fields.update(overrides)
    return base.__class__(**fields)


class B7InterplanetaryTests(unittest.TestCase):
    """Guards the B7 interplanetary extensions of the shared B5 machine
    (design section 5.8, adapted to the native-warp architecture): the
    param-selected interplanetary plan action, the hyperbolic ejection
    burn-done gate, via-body coast legality + warp, the time-to-SOI
    correction triggers with the native-first trigger approach, the
    return-body flyby terminal, the exit-body assertion report, a full B7
    happy-path walk, and the defaults-preserve-B5 contract."""

    def test_rails_frames_do_not_consume_the_nostart_budget(self):
        # Finding 19 (fifth flight): a round granted from a 100,000x coast
        # enters CORRECTION-BURN mid-ramp-down and the GAME-time no-start
        # budget evaporated in two polls -- both rounds consumed with the
        # plan unburned and apErr frozen. Rails frames must re-anchor the
        # clock; it counts only from the first non-rails frame.
        state = _b7_state(mlib.B5_CORRECTION_BURN, planned_node_count=1)
        # Two rails ramp-down polls spanning 40,000 game-s: NO give-up.
        state, _ = mlib.b5_decide(state, snap(
            ut=10_000.0, body="Sun", node_count=1, node_dv=108.7,
            warp_mode="RAILS", warp_rate=100_000.0))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        state, _ = mlib.b5_decide(state, snap(
            ut=50_000.0, body="Sun", node_count=1, node_dv=108.7,
            warp_mode="RAILS", warp_rate=48_000.0))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        self.assertEqual(state.corr_nostart_anchor_ut, 50_000.0)
        # 19b: the decay TAIL reads mode PHYSICS at >4x (KSP flips
        # TimeWarp.Mode to LOW immediately while CurrentRate still decays
        # from 100,000) -- high-RATE frames re-anchor regardless of label.
        state, _ = mlib.b5_decide(state, snap(
            ut=55_000.0, body="Sun", node_count=1, node_dv=108.7,
            warp_mode="PHYSICS", warp_rate=5.32))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        self.assertEqual(state.corr_nostart_anchor_ut, 55_000.0)
        # Ramp done: genuine flip frames (<= 4x) COUNT. Inside the budget
        # stays; past it gives the round up (bounded).
        state, _ = mlib.b5_decide(state, snap(
            ut=55_100.0, body="Sun", node_count=1, node_dv=108.7,
            warp_mode="PHYSICS", warp_rate=2.0))
        self.assertEqual(state.phase, mlib.B5_CORRECTION_BURN)
        state, _ = mlib.b5_decide(state, snap(
            ut=55_000.0 + B7_PARAMS.burn_nostart_seconds + 1.0,
            body="Sun", node_count=1, node_dv=108.7,
            warp_mode="PHYSICS", warp_rate=2.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 1)

    def test_native_warp_retarget_is_asymmetric(self):
        # B7 review MINOR-4: a LATER fresh target within 2% of the remaining
        # span holds (proportional SOI-estimate jitter must not churn the
        # warp socket), while an EARLIER shift beyond the 120 s floor still
        # retargets promptly (a stale later target would carry the warp past
        # the boundary at speed).
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", altitude=8_000_000.0,
            time_to_soi=200_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 199_980.0)])
        # LATER by 460 s on a ~194k span (2% = ~3,880): HOLD.
        state, actions = mlib.b5_decide(state, snap(
            ut=6_000.0, body="Kerbin", altitude=9_000_000.0,
            time_to_soi=194_470.0, warping_to=199_980.0,
            warp_mode="RAILS", warp_rate=10_000.0))
        self.assertEqual(actions, [])
        # LATER beyond 2%: re-issue.
        state, actions = mlib.b5_decide(state, snap(
            ut=6_010.0, body="Kerbin", altitude=9_000_000.0,
            time_to_soi=198_470.0, warping_to=199_980.0,
            warp_mode="RAILS", warp_rate=10_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 204_450.0)])

    def test_no_encounter_coast_fires_the_round_early(self):
        # Finding 18 (fourth flight): the phase-angle ejection produced NO
        # Duna encounter -- tts NaN across the whole heliocentric coast --
        # so the time triggers never fired and the coast flaked past Duna's
        # orbit. A debounced encounter-less time-mode coast over a via body
        # now fires the pending round early so the course-correct plan can
        # CREATE the encounter.
        state = _b7_state(mlib.B5_COAST_TO_TARGET)
        for i in range(mlib.NO_ENCOUNTER_DEBOUNCE_FRAMES - 1):
            state, _ = mlib.b5_decide(state, snap(
                ut=10.0 + i, body="Sun", altitude=13_500_000_000.0))
            self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.no_encounter_streak,
                         mlib.NO_ENCOUNTER_DEBOUNCE_FRAMES - 1)
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, body="Sun", altitude=13_500_000_000.0))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        self.assertEqual(state.no_encounter_streak, 0)
        self.assertIn(Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT,
                             B7_PARAMS.course_correct_periapsis,
                             limit=B7_PARAMS.max_correction_dv), actions)

    def test_no_encounter_gate_fail_closed_terms(self):
        cases = (
            dict(time_to_soi=25_000_000.0),  # encounter EXISTS (above the round-0
                                             # threshold): the time trigger owns
                                             # the eventual fire, not this gate
            dict(body="Kerbin"),             # home SOI escape: not a via body
            dict(node_count=1,
                 node_ut=100_000.0),         # a node is pending
        )
        for kw in cases:
            state = _b7_state(mlib.B5_COAST_TO_TARGET)
            fired = False
            for i in range(mlib.NO_ENCOUNTER_DEBOUNCE_FRAMES + 2):
                s = dict(ut=10.0 + i, body="Sun", altitude=13_500_000_000.0)
                s.update(kw)
                state, _ = mlib.b5_decide(state, snap(**s))
                fired = fired or state.phase == mlib.B5_PLAN_CORRECTION
            self.assertFalse(fired, "gate fired for %r" % (kw,))
            self.assertEqual(state.no_encounter_streak, 0)
        # Rounds exhausted: never fires (bounded).
        state = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        for i in range(mlib.NO_ENCOUNTER_DEBOUNCE_FRAMES + 2):
            state, _ = mlib.b5_decide(state, snap(
                ut=10.0 + i, body="Sun", altitude=13_500_000_000.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)

    def test_no_encounter_gate_is_time_mode_only(self):
        # B5/B6 (altitude mode): a NaN-tts home coast must never fire it.
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1)
        for i in range(mlib.NO_ENCOUNTER_DEBOUNCE_FRAMES + 2):
            state, _ = mlib.b5_decide(state, snap(
                ut=10.0 + i, body="Kerbin", altitude=2_000_000.0))
        self.assertEqual(state.no_encounter_streak, 0)
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)

    def test_orbit_emits_interplanetary_plan(self):
        # B7: ORBIT plans via OperationInterplanetaryTransfer...
        state = _b7_state(mlib.B5_ORBIT)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_PLAN_TRANSFER)
        self.assertEqual(actions, [
            Action(mlib.ACTION_SET_TARGET_BODY, text="Duna"),
            Action(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER)])
        # ...and the bounded cadence RE-plans with the same interplanetary
        # action (plus the PLAN rails hold on the first cadence frame).
        state, actions = mlib.b5_decide(state, snap(ut=45.0, body="Kerbin",
                                                    altitude=700_000.0))
        self.assertEqual(actions, [
            Action(mlib.ACTION_SET_RAILS_WARP, 2.0),
            Action(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER)])
        # B5 params still emit the moon Hohmann plan, never the interplanetary.
        b5s = _b5_state(mlib.B5_ORBIT)
        _, b5a = mlib.b5_decide(b5s, snap(ut=10.0, body="Kerbin"))
        self.assertIn(Action(mlib.ACTION_MJ_PLAN_TRANSFER), b5a)
        self.assertNotIn(Action(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER), b5a)

    def test_hyperbolic_burn_done_gate(self):
        # ecc >= floor in home SOI (node consumed): exit to COAST -- the
        # escape drove the home-frame apoapsis negative, which the old
        # apoapsis floor could never certify.
        state = _b7_state(mlib.B5_TRANSFER_BURN, planned_node_count=1)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", node_count=0, eccentricity=1.2,
            apoapsis=-40_000_000.0, periapsis=695_000.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        # Sub-hyperbolic ecc: stay (the ejection has not escaped yet).
        state = _b7_state(mlib.B5_TRANSFER_BURN, planned_node_count=1)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", node_count=0, eccentricity=0.9,
            apoapsis=80_000.0))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        # Already left home SOI (body is a via body): exit regardless of the
        # heliocentric-frame ecc (< 1, which would falsely fail the ecc leg).
        state = _b7_state(mlib.B5_TRANSFER_BURN, planned_node_count=1)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0, body="Sun", node_count=0, eccentricity=0.2))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        # NaN ecc in home SOI fails CLOSED: never an exit on no evidence.
        state = _b7_state(mlib.B5_TRANSFER_BURN, planned_node_count=1)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", node_count=0,
            eccentricity=float("nan")))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        # B5 params (ecc floor 0): the apoapsis floor still owns the exit --
        # a hyperbolic ecc reading alone must NOT exit under the floor...
        state = _b5_state(mlib.B5_TRANSFER_BURN, planned_node_count=1)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", node_count=0, eccentricity=1.5,
            apoapsis=90_000.0))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)
        # ...and the floor alone still does.
        state, _ = mlib.b5_decide(state, snap(
            ut=20.0, body="Kerbin", node_count=0, eccentricity=0.0,
            apoapsis=11_000_000.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)

    def test_via_body_is_not_ejection(self):
        # body="Sun" (a via body) stays in the coast: a legal intermediate SOI.
        state = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Sun",
                                              altitude=13_000_000_000.0))
        self.assertFalse(state.done)
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        # A truly foreign body still ASSERT-FAILs.
        state = _b7_state(mlib.B5_COAST_TO_TARGET)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Eve"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("left home SOI", state.loss_reason)
        self.assertIn("Eve", state.loss_reason)

    def test_coast_warps_over_via_body(self):
        # Heliocentric coast (rounds spent, no encounter): the held factor-7
        # coast flows -- the Sun legality row (factor 7 >= 65,400 km) must
        # NOT clamp a distant heliocentric craft down.
        state = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Sun", altitude=13_000_000_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 7.0)])
        # Close to the Sun the same row DOES clamp (30,000 km legalizes
        # exactly factor 5) -- the table is live, not a fail-open bypass.
        state = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Sun", altitude=30_000_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 5.0)])
        # body="" still HOLDS with no warp change (blank dwell counted).
        state = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=2)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body=""))
        self.assertEqual(actions, [])
        self.assertEqual(state.body_blank_count, 1)

    def test_time_to_soi_correction_trigger(self):
        # Round 0 (threshold 20M) fires on the first heliocentric frame with
        # an encounter (tof ~7M is already below it).
        state = _b7_state(mlib.B5_COAST_TO_TARGET)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Sun", altitude=13_000_000_000.0,
            time_to_soi=6_000_000.0))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        self.assertEqual(actions[-1], Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT,
                                             50_000.0, limit=200.0))
        # The home-SOI escape: a small time_to_soi there is the KERBIN SOI
        # edge, never a correction trigger -- the coast natively warps to the
        # exit instead (soi_lead short of the boundary).
        state = _b7_state(mlib.B5_COAST_TO_TARGET)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Kerbin", altitude=1_000_000.0,
            time_to_soi=50_000.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 49_980.0)])
        # Round 1 (threshold 500k) does NOT fire while tts is above it...
        state = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0, body="Sun", altitude=13_000_000_000.0,
            time_to_soi=6_000_000.0))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        # ...and fires once tts falls at/below it.
        state = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0, body="Sun", altitude=13_000_000_000.0,
            time_to_soi=400_000.0))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        # NaN tts (no encounter) NEVER triggers -- fail closed; the doomed
        # OperationCourseCorrection (which needs an encounter) is never asked.
        state = _b7_state(mlib.B5_COAST_TO_TARGET)
        state, _ = mlib.b5_decide(state, snap(
            ut=10.0, body="Sun", altitude=13_000_000_000.0,
            time_to_soi=float("nan")))
        self.assertEqual(state.phase, mlib.B5_COAST_TO_TARGET)
        self.assertEqual(state.correction_rounds_done, 0)

    def test_time_mode_trigger_approach_native_then_stair(self):
        # FAR from the trigger: ONE native warp straight to the trigger UT
        # (time_to_soi falls 1:1 with UT, so trigger UT = now + tts -
        # threshold). The target IS the trigger instant -- arrival is
        # followed by a poll that fires the round, never a warp PAST it.
        state = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1)
        state, actions = mlib.b5_decide(state, snap(
            ut=1_000.0, body="Sun", altitude=13_000_000_000.0,
            time_to_soi=2_000_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_WARP_TO_UT, 1_501_000.0)])
        self.assertEqual(state.warp_to_cmd, 1_501_000.0)
        self.assertEqual(state.warp_cmd, 0)
        # Arrival poll: tts is now at the threshold -> the round fires (the
        # trigger prelude cancels the completed/expected native warp).
        state, actions = mlib.b5_decide(state, snap(
            ut=1_501_000.0, body="Sun", altitude=13_000_000_000.0,
            time_to_soi=499_999.0))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        self.assertEqual(actions[0], Action(mlib.ACTION_CANCEL_WARP))
        # NEAR the trigger (inside soi_lead): the rails time stair floored at
        # factor 2 (the altitude mode's no-1x floor -- a trigger is a
        # refinement point, not a wall).
        fresh = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1)
        fresh, actions = mlib.b5_decide(fresh, snap(
            ut=10.0, body="Sun", altitude=13_000_000_000.0,
            time_to_soi=500_020.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 2.0)])
        self.assertIsNone(fresh.warp_to_cmd)
        # NaN tts fails closed: no native warp to a bogus target -- the held
        # coast factor flows instead (the no-encounter fallback).
        fresh = _b7_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1)
        fresh, actions = mlib.b5_decide(fresh, snap(
            ut=10.0, body="Sun", altitude=13_000_000_000.0))
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 7.0)])
        self.assertIsNone(fresh.warp_to_cmd)

    def test_flyby_returns_to_exit_body(self):
        # body == return_body (Sun) after the flyby: the RETURN terminal.
        state = _b7_state(mlib.B5_TARGET_FLYBY, warp_cmd=5)
        state, actions = mlib.b5_decide(state, snap(
            ut=10.0, body="Sun", altitude=13_000_000_000.0))
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B5_RETURN)
        self.assertIsNone(state.verdict)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])
        # body == Kerbin (home, but NOT the exit) is off-course for B7.
        state = _b7_state(mlib.B5_TARGET_FLYBY)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("ejected", state.loss_reason)
        # B5 params: the free-return into Kerbin SOI still RETURNs.
        state = _b5_state(mlib.B5_TARGET_FLYBY)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, body="Kerbin",
                                              altitude=3_000_000.0))
        self.assertEqual(state.phase, mlib.B5_RETURN)

    def test_returned_assertion_reports_exit_body(self):
        phases = (mlib.B5_ORBIT, mlib.B5_TARGET_FLYBY, mlib.B5_RETURN)
        outs = mlib.evaluate_b5_assertions(
            [], B7_PARAMS, phases_reached=phases, min_target_altitude=60_000.0)
        by_name = {o.name: o for o in outs}
        # Name kept (schema stability); value + detail carry the EXIT body.
        self.assertEqual(by_name["returnedToHome"].value, "Sun")
        self.assertEqual(by_name["returnedToHome"].detail["returnBody"], "Sun")
        self.assertTrue(by_name["returnedToHome"].met)
        b5 = {o.name: o for o in mlib.evaluate_b5_assertions(
            [], B5_PARAMS, phases_reached=phases, min_target_altitude=60_000.0)}
        self.assertEqual(b5["returnedToHome"].value, "Kerbin")
        self.assertEqual(b5["returnedToHome"].detail["returnBody"], "Kerbin")

    def test_full_happy_path_walk_b7(self):
        state = mlib.b5_initial_state(B7_PARAMS)
        frames = [
            snap(ut=0.0, body="Kerbin"),                                 # 0 PRELAUNCH->MJ-ASCENT
            snap(ut=300.0, apoapsis=690_000.0, mj_ascent_complete=True,
                 body="Kerbin"),                                         # 1 ->CIRCULARIZE
            snap(ut=400.0, apoapsis=700_000.0, periapsis=690_000.0,
                 body="Kerbin"),                                         # 2 ->ORBIT
            snap(ut=401.0, apoapsis=700_000.0, periapsis=690_000.0,
                 body="Kerbin"),                                         # 3 ORBIT->PLAN (target+interplanetary plan)
            snap(ut=405.0, apoapsis=700_000.0, periapsis=690_000.0,
                 body="Kerbin", node_count=1),                           # 4 node -> TRANSFER-BURN (execute)
            snap(ut=10_000_000.0, apoapsis=705_000.0, periapsis=690_000.0,
                 body="Kerbin", node_count=1),                           # 5 window autowarp (executor owns it)
            snap(ut=10_000_600.0, apoapsis=-40_000_000.0,
                 periapsis=695_000.0, eccentricity=1.4,
                 altitude=800_000.0, body="Kerbin", node_count=0),       # 6 hyperbolic -> COAST
            snap(ut=10_000_700.0, eccentricity=1.4, altitude=1_200_000.0,
                 body="Kerbin", time_to_soi=50_000.0),                   # 7 escape leg: native warp to Kerbin SOI exit
            snap(ut=10_060_000.0, body="Sun",
                 altitude=13_000_000_000.0, time_to_soi=7_000_000.0),    # 8 helio + encounter: round 0 (20M) fires
            snap(ut=10_060_010.0, body="Sun",
                 altitude=13_000_000_100.0, node_count=1),               # 9 node -> CORRECTION-BURN (AP point)
            snap(ut=10_060_020.0, body="Sun", altitude=13_000_000_200.0,
                 node_count=1, node_dv=80.0, ap_error=1.5),              # 10 settling (streak 1) -> flip phys warp
            snap(ut=10_060_040.0, body="Sun", altitude=13_000_000_300.0,
                 node_count=1, node_dv=80.0, ap_error=1.2,
                 warp_mode="PHYSICS", warp_rate=2.0),                    # 11 settled + streak 2 -> drop phys warp
            snap(ut=10_060_042.0, body="Sun", altitude=13_000_000_400.0,
                 node_count=1, node_dv=80.0, ap_error=1.1),              # 12 warp NONE -> throttle
            snap(ut=10_060_060.0, body="Sun", altitude=13_000_000_500.0,
                 node_count=1, node_dv=40.0, ap_error=1.0),              # 13 burning
            snap(ut=10_060_070.0, body="Sun", altitude=13_000_000_600.0,
                 node_count=1, node_dv=1.5, ap_error=1.0),               # 14 dv <= cut -> cut triple, COAST
            snap(ut=10_100_000.0, body="Sun", altitude=13_000_001_000.0,
                 time_to_soi=6_900_000.0, next_body="Duna",
                 next_pe=100_000.0),                                     # 15 round-1 approach: native to the trigger
            snap(ut=16_500_000.0, body="Sun", altitude=13_000_002_000.0,
                 time_to_soi=499_995.0, next_body="Duna",
                 next_pe=100_000.0),                                     # 16 trigger 500k -> round 1
            snap(ut=16_500_010.0, body="Sun", altitude=13_000_003_000.0,
                 node_count=1),                                          # 17 node -> CORRECTION-BURN (AP point)
            snap(ut=16_500_020.0, body="Sun", altitude=13_000_004_000.0,
                 node_count=1, node_dv=10.0, ap_error=1.4),              # 18 settling (streak 1) -> flip phys warp
            snap(ut=16_500_040.0, body="Sun", altitude=13_000_005_000.0,
                 node_count=1, node_dv=10.0, ap_error=1.2,
                 warp_mode="PHYSICS", warp_rate=2.0),                    # 19 settled + streak 2 -> drop phys warp
            snap(ut=16_500_042.0, body="Sun", altitude=13_000_006_000.0,
                 node_count=1, node_dv=10.0, ap_error=1.1),              # 20 warp NONE -> throttle
            snap(ut=16_500_060.0, body="Sun",
                 altitude=13_000_007_000.0, node_count=0),               # 21 node consumed -> cut pair, COAST
            snap(ut=16_600_000.0, body="Sun", altitude=13_000_008_000.0,
                 time_to_soi=380_000.0, next_body="Duna",
                 next_pe=40_000.0),                                      # 22 rounds spent; HEALTHY predicted arrival
                                                                         #    (finding-16 gate stays quiet) -> native to Duna SOI
            snap(ut=17_000_000.0, body="Duna", altitude=40_000_000.0),   # 23 Duna SOI -> TARGET-FLYBY
            snap(ut=17_010_000.0, body="Duna", altitude=20_000_000.0,
                 periapsis=60_000.0),                                    # 24 outer leg: rails stair (no exit estimate)
            snap(ut=17_050_000.0, body="Duna", altitude=60_000.0,
                 periapsis=55_000.0, warp_mode="RAILS",
                 warp_rate=10_000.0),                                    # 25 periapsis area: floor + Duna legality clamp
            snap(ut=17_200_000.0, body="Sun",
                 altitude=13_000_010_000.0),                             # 26 exit SOI (Sun) -> RETURN terminal
        ]
        state, per_frame = drive_b5(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.B5_RETURN)
        self.assertIsNone(state.verdict)
        self.assertIsNone(state.loss_reason)
        self.assertEqual(state.min_target_altitude, 60_000.0)
        self.assertEqual(state.correction_rounds_done, 2)
        # Finding-16 coexistence: the healthy predicted arrival (frame 22,
        # next_pe 40 km >= the 15 km floor) never armed the extra-round gate.
        self.assertEqual(state.extra_rounds_done, 0)
        self.assertEqual(state.arrival_bad_streak, 0)
        self.assertEqual(state.phases_reached,
                         (mlib.B5_PRELAUNCH, mlib.B5_MJ_ASCENT, mlib.B5_CIRCULARIZE,
                          mlib.B5_ORBIT, mlib.B5_PLAN_TRANSFER, mlib.B5_TRANSFER_BURN,
                          mlib.B5_COAST_TO_TARGET,
                          mlib.B5_PLAN_CORRECTION, mlib.B5_CORRECTION_BURN,
                          mlib.B5_COAST_TO_TARGET,
                          mlib.B5_PLAN_CORRECTION, mlib.B5_CORRECTION_BURN,
                          mlib.B5_COAST_TO_TARGET, mlib.B5_TARGET_FLYBY,
                          mlib.B5_RETURN))
        self.assertEqual(per_frame[3], [
            Action(mlib.ACTION_SET_TARGET_BODY, text="Duna"),
            Action(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER)])
        self.assertEqual(per_frame[4], [Action(mlib.ACTION_MJ_EXECUTE_NODES)])
        self.assertEqual(per_frame[5], [])   # executor owns the window autowarp
        self.assertEqual(per_frame[6], [])   # hyperbolic exit transition frame
        # Kerbin escape leg: native warp to the home SOI exit minus soi_lead.
        self.assertEqual(per_frame[7],
                         [Action(mlib.ACTION_WARP_TO_UT, 10_050_670.0)])
        # Round 0 entry cancels the (expected-)active native warp in the
        # prelude, then plans the 50 km correction under the 200 m/s cap.
        self.assertEqual(per_frame[8], [
            Action(mlib.ACTION_CANCEL_WARP),
            Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT, 50_000.0, limit=200.0)])
        self.assertEqual(per_frame[9], [Action(mlib.ACTION_AP_POINT_NODE)])
        self.assertEqual(per_frame[10], [Action(mlib.ACTION_SET_PHYSICS_WARP, 1.0)])
        self.assertEqual(per_frame[11], [Action(mlib.ACTION_SET_PHYSICS_WARP, 0.0)])
        self.assertEqual(per_frame[12], [Action(mlib.ACTION_SET_THROTTLE, 0.25)])
        self.assertEqual(per_frame[13], [])
        self.assertEqual(per_frame[14], [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                         Action(mlib.ACTION_AP_DISENGAGE),
                                         Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)])
        # Round-1 approach: native warp straight to the trigger UT
        # (tts 6.9M - threshold 500k = 6.4M ahead).
        self.assertEqual(per_frame[15],
                         [Action(mlib.ACTION_WARP_TO_UT, 16_500_000.0)])
        self.assertEqual(per_frame[16], [
            Action(mlib.ACTION_CANCEL_WARP),
            Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT, 50_000.0, limit=200.0)])
        self.assertEqual(per_frame[17], [Action(mlib.ACTION_AP_POINT_NODE)])
        self.assertEqual(per_frame[20], [Action(mlib.ACTION_SET_THROTTLE, 0.25)])
        self.assertEqual(per_frame[21], [Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                                         Action(mlib.ACTION_AP_DISENGAGE)])
        # Post-rounds coast: native warp to the Duna SOI boundary minus lead.
        self.assertEqual(per_frame[22],
                         [Action(mlib.ACTION_WARP_TO_UT, 16_979_970.0)])
        self.assertEqual(per_frame[23], [])  # SOI-entry transition frame
        self.assertEqual(per_frame[24], [Action(mlib.ACTION_SET_RAILS_WARP, 6.0)])
        # Periapsis area: the flyby floor wants 100x but the Duna table only
        # legalizes 50x at 60 km -- command the ACHIEVABLE factor 3.
        self.assertEqual(per_frame[25], [Action(mlib.ACTION_SET_RAILS_WARP, 3.0)])
        self.assertEqual(per_frame[26], [Action(mlib.ACTION_SET_RAILS_WARP, 0.0)])
        # All four assertions met; the flight resolves MISSION-OK.
        outs = mlib.evaluate_b5_assertions(
            [], B7_PARAMS, phases_reached=state.phases_reached,
            min_target_altitude=state.min_target_altitude)
        self.assertTrue(mlib.all_assertions_met(outs))
        verdict, reason = mlib.resolve_flight_verdict(state, outs)
        self.assertEqual(verdict, mlib.MISSION_OK)

    def test_b5_paths_unchanged_with_b7_defaults(self):
        # The five new params default OFF...
        p = mlib.b5_params_from_dict({})
        self.assertEqual(p.via_bodies, ())
        self.assertEqual(p.return_body, "")
        self.assertFalse(p.interplanetary_transfer)
        self.assertEqual(p.ejection_ecc_floor, 0.0)
        self.assertEqual(p.correction_trigger_time_to_soi, ())
        # ...and every helper collapses to the pre-B7 B5 value.
        self.assertEqual(mlib._b5_coast_bodies(B5_PARAMS), ("", "Kerbin"))
        self.assertEqual(mlib._b5_warp_bodies(B5_PARAMS), ("Kerbin",))
        self.assertEqual(mlib._b5_return_body(B5_PARAMS), "Kerbin")
        self.assertEqual(mlib._b5_transfer_plan_action(B5_PARAMS),
                         Action(mlib.ACTION_MJ_PLAN_TRANSFER))
        self.assertEqual(mlib._b5_correction_triggers(B5_PARAMS),
                         B5_PARAMS.correction_trigger_alts)
        # Representative B5 transitions re-run byte-identical:
        # (a) a foreign body (Sun) in the coast is still the ejected terminal;
        state = _b5_state(mlib.B5_COAST_TO_TARGET)
        state, actions = mlib.b5_decide(state, snap(ut=10.0, body="Sun"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertEqual(actions, [])
        # (b) the altitude trigger still enters PLAN-CORRECTION with the same
        # prelude + plan action;
        state = _b5_state(mlib.B5_COAST_TO_TARGET, correction_rounds_done=1)
        state, actions = mlib.b5_decide(state, snap(
            ut=20.0, altitude=6_200_000.0, body="Kerbin"))
        self.assertEqual(state.phase, mlib.B5_PLAN_CORRECTION)
        self.assertEqual(actions, [Action(mlib.ACTION_SET_RAILS_WARP, 2.0),
                                   Action(mlib.ACTION_MJ_PLAN_COURSE_CORRECT,
                                          60000.0, limit=150.0)])
        # (c) TRANSFER-BURN still holds under the apoapsis floor.
        state = _b5_state(mlib.B5_TRANSFER_BURN)
        state, _ = mlib.b5_decide(state, snap(ut=10.0, apoapsis=90_000.0,
                                              body="Kerbin", node_count=0))
        self.assertEqual(state.phase, mlib.B5_TRANSFER_BURN)


# ===========================================================================
# FORGE (fixture-forge) machine + assertions.
# ===========================================================================


FORGE_PARAMS = mlib.ForgeParams(
    craft_name="Kerbal X",
    launch_site="LaunchPad",
    launch_timeout=120.0,
    settle_debounce=3,
)


def drive_forge(state, frames):
    per_frame = []
    for f in frames:
        state, actions = mlib.forge_decide(state, f)
        per_frame.append(actions)
    return state, per_frame


class ForgeMachineTests(unittest.TestCase):
    """Guards the FIXTURE-FORGE machine: launch the craft, settle PRELAUNCH, done
    MISSION-OK. A premature done or a vessel_lost false-terminal during the reload
    would forge no fixture or red a good stamp."""

    def test_prelaunch_emits_launch_vessel(self):
        state = mlib.forge_initial_state(FORGE_PARAMS)
        new, actions = mlib.forge_decide(state, snap(ut=0.0))
        self.assertEqual(new.phase, mlib.FORGE_LAUNCH)
        self.assertEqual(actions, [Action(mlib.ACTION_LAUNCH_VESSEL,
                                           text="Kerbal X",
                                           launch_site="LaunchPad", crew=None)])

    def test_full_happy_path_settles_prelaunch(self):
        state = mlib.forge_initial_state(FORGE_PARAMS)
        frames = [
            snap(ut=0.0),                                   # PRELAUNCH->LAUNCH
            snap(ut=5.0, situation="PRE_LAUNCH"),           # settle 1
            snap(ut=10.0, situation="PRE_LAUNCH"),          # settle 2
            snap(ut=15.0, situation="PRE_LAUNCH"),          # settle 3 -> SETTLED (done)
        ]
        state, _ = drive_forge(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.FORGE_SETTLED)
        self.assertIsNone(state.verdict)
        self.assertEqual(state.phases_reached,
                         (mlib.FORGE_PRELAUNCH, mlib.FORGE_LAUNCH, mlib.FORGE_SETTLED))

    def test_settle_debounce_resets_on_non_settled_frame(self):
        # A transient non-settle frame mid-reload resets the streak (no premature done).
        state = mlib.forge_initial_state(FORGE_PARAMS)
        state, _ = mlib.forge_decide(state, snap(ut=0.0))
        state, _ = mlib.forge_decide(state, snap(ut=5.0, situation="PRE_LAUNCH"))
        state, _ = mlib.forge_decide(state, snap(ut=6.0, situation="FLYING"))  # reset
        self.assertEqual(state.settle_streak, 0)
        self.assertEqual(state.phase, mlib.FORGE_LAUNCH)

    def test_vessel_lost_during_launch_is_transient_not_terminal(self):
        # launch_vessel is a scene reload; a vessel_lost snapshot mid-reload must NOT
        # terminate -- the settle debounce + launch budget own the outcome.
        state = mlib.forge_initial_state(FORGE_PARAMS)
        state, _ = mlib.forge_decide(state, snap(ut=0.0))
        state, _ = mlib.forge_decide(state, snap(ut=2.0, vessel_lost=True))
        self.assertFalse(state.done)
        self.assertEqual(state.phase, mlib.FORGE_LAUNCH)
        self.assertEqual(state.settle_streak, 0)

    def test_vessel_lost_before_launch_is_terminal(self):
        state = mlib.forge_initial_state(FORGE_PARAMS)
        state, _ = mlib.forge_decide(state, snap(ut=0.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", state.loss_reason)

    def test_launch_timeout_flakes(self):
        state = mlib.forge_initial_state(FORGE_PARAMS)
        state, _ = mlib.forge_decide(state, snap(ut=0.0))
        # No settle situation ever; past launch_timeout -> FLAKE naming LAUNCH.
        state, _ = mlib.forge_decide(state, snap(ut=200.0, situation="FLYING"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.FORGE_LAUNCH)


class ForgeAssertionTests(unittest.TestCase):
    def test_both_assertions_met(self):
        frames = [snap(situation="PRE_LAUNCH")]
        outs = mlib.evaluate_forge_assertions(
            frames, FORGE_PARAMS,
            phases_reached=(mlib.FORGE_PRELAUNCH, mlib.FORGE_LAUNCH, mlib.FORGE_SETTLED))
        self.assertEqual([o.name for o in outs], ["launched", "settledOnPad"])
        self.assertTrue(all(o.met for o in outs))

    def test_settled_unmet_without_settled_phase(self):
        # Launched but never settled (no FORGE_SETTLED) -> settledOnPad unmet.
        outs = mlib.evaluate_forge_assertions(
            [snap(situation="FLYING")], FORGE_PARAMS,
            phases_reached=(mlib.FORGE_PRELAUNCH, mlib.FORGE_LAUNCH))
        self.assertTrue(outs[0].met)      # launched
        self.assertFalse(outs[1].met)     # settledOnPad

    def test_settled_unmet_with_wrong_final_situation(self):
        # Reached SETTLED but the final frame situation is not a settle situation
        # (fail-closed: the persisted state would not be a clean PRELAUNCH pad).
        outs = mlib.evaluate_forge_assertions(
            [snap(situation="FLYING")], FORGE_PARAMS,
            phases_reached=(mlib.FORGE_PRELAUNCH, mlib.FORGE_LAUNCH, mlib.FORGE_SETTLED))
        self.assertFalse(outs[1].met)


# ===========================================================================
# FORGE-LKO (orbital fixture-forge) machine + assertions.
# ===========================================================================


FLKO_PARAMS = mlib.ForgeLkoParams(
    craft_name="Kerbal X",
    launch_site="LaunchPad",
    crew_names=("Valentina Kerman", "Bob Kerman"),
    min_crew=2,
    launch_timeout=300.0,
    launch_settle_debounce=2,
    target_apoapsis=100000.0,
    target_periapsis=100000.0,
    apo_error=10000.0,
    peri_error=10000.0,
    ascent_timeout=900.0,
    circularize_timeout=2400.0,
    separation_timeout=120.0,
    park_dwell=60.0,
    park_timeout=600.0,
    park_debounce=2,
    max_angular_velocity=0.05,
    min_safe_periapsis=75000.0,
)


def _flko_params(**overrides):
    return FLKO_PARAMS.__class__(**{**FLKO_PARAMS.__dict__, **overrides})


def drive_flko(state, frames):
    per_frame = []
    for f in frames:
        state, actions = mlib.forge_lko_decide(state, f)
        per_frame.append(actions)
    return state, per_frame


def _flko_parked(ut, **kw):
    """A snapshot that satisfies every PARK stability conjunct."""
    fields = dict(ut=ut, situation="ORBITING", apoapsis=100000.0,
                  periapsis=99000.0, angular_velocity=0.001, crew_count=2)
    fields.update(kw)
    return snap(**fields)


def _flko_to_park(params=None):
    """Drive the machine from PRELAUNCH to the head of PARK and return the state."""
    state = mlib.forge_lko_initial_state(params or FLKO_PARAMS)
    frames = [
        snap(ut=0.0, situation="FLYING"),                                  # -> LAUNCH
        snap(ut=5.0, situation="PRE_LAUNCH", crew_count=2),                # settle 1
        snap(ut=10.0, situation="PRE_LAUNCH", crew_count=2),               # settle 2 -> ASCENT
        snap(ut=300.0, apoapsis=99000.0, mj_ascent_complete=True),         # -> CIRCULARIZE
        snap(ut=400.0, periapsis=99000.0, vessel_count=1),                 # -> SEPARATE (baseline 1)
        snap(ut=401.0, vessel_count=2, available_thrust=0.0),              # split 1
        snap(ut=402.0, vessel_count=2, available_thrust=0.0),              # split 2
        snap(ut=403.0, vessel_count=2, available_thrust=0.0),              # split 3 -> ignite
        snap(ut=404.0, vessel_count=2, available_thrust=200000.0),         # thrust 1
        snap(ut=405.0, vessel_count=2, available_thrust=200000.0),         # thrust 2
        snap(ut=406.0, vessel_count=2, available_thrust=200000.0),         # thrust 3 -> PARK
    ]
    state, per_frame = drive_flko(state, frames)
    return state, per_frame


class SeparationEvidenceTests(unittest.TestCase):
    """Guards the SHARED two-step separation counter (B-DOCK + FORGE-LKO). A
    regression here certifies a separation that never happened (a full stack
    docking / an uncontrollable fixture) or an ignition that never lit."""

    def test_split_needs_debounce_then_latches(self):
        settle, thrust, confirmed, ignited = mlib.separation_evidence(
            2, float("nan"), 1, 0, 0, False, debounce=3)
        self.assertEqual((settle, confirmed, ignited), (1, False, False))
        settle, thrust, confirmed, ignited = mlib.separation_evidence(
            2, float("nan"), 1, 2, 0, False, debounce=3)
        self.assertTrue(confirmed)
        # Latched: a later frame whose count dips back never un-confirms.
        _, _, confirmed2, _ = mlib.separation_evidence(
            1, float("nan"), 1, 0, 0, True, debounce=3)
        self.assertTrue(confirmed2)

    def test_unread_count_never_bumps_the_baseline(self):
        # vessel_count defaults 0 (unread): it can never exceed a real baseline.
        settle, _, confirmed, _ = mlib.separation_evidence(
            0, 1.0, 1, 5, 0, False, debounce=3)
        self.assertEqual(settle, 0)
        self.assertFalse(confirmed)

    def test_nan_thrust_is_never_ignited(self):
        _, thrust, _, ignited = mlib.separation_evidence(
            2, float("nan"), 1, 0, 5, True, debounce=3)
        self.assertEqual(thrust, 0)
        self.assertFalse(ignited)

    def test_thrust_debounce_ignites(self):
        _, thrust, _, ignited = mlib.separation_evidence(
            2, 200000.0, 1, 3, 2, True, debounce=3)
        self.assertEqual(thrust, 3)
        self.assertTrue(ignited)


class ForgeLkoMachineTests(unittest.TestCase):
    """Guards the ORBITAL fixture forge: a bad stamp is worse than a flake -- every
    consumer of the fixture inherits it."""

    def test_prelaunch_emits_launch_vessel_with_named_crew(self):
        state = mlib.forge_lko_initial_state(FLKO_PARAMS)
        new, actions = mlib.forge_lko_decide(state, snap(ut=0.0))
        self.assertEqual(new.phase, mlib.FLKO_LAUNCH)
        self.assertEqual(actions, [Action(mlib.ACTION_LAUNCH_VESSEL,
                                          text="Kerbal X", launch_site="LaunchPad",
                                          crew=("Valentina Kerman", "Bob Kerman"))])

    def test_launch_settle_requires_the_crew_gate(self):
        # On the pad with NO crew read (the -1 unread sentinel) the settle streak
        # never advances: an uncrewed stamp must never reach the ascent.
        state = mlib.forge_lko_initial_state(FLKO_PARAMS)
        state, _ = mlib.forge_lko_decide(state, snap(ut=0.0))
        for ut in (5.0, 10.0, 15.0):
            state, _ = mlib.forge_lko_decide(
                state, snap(ut=ut, situation="PRE_LAUNCH"))
        self.assertEqual(state.phase, mlib.FLKO_LAUNCH)
        self.assertEqual(state.launch_settle_streak, 0)

    def test_launch_crew_short_flake_names_the_crew(self):
        state = mlib.forge_lko_initial_state(FLKO_PARAMS)
        state, _ = mlib.forge_lko_decide(state, snap(ut=0.0))
        state, _ = mlib.forge_lko_decide(
            state, snap(ut=5.0, situation="PRE_LAUNCH", crew_count=0))
        state, _ = mlib.forge_lko_decide(
            state, snap(ut=1000.0, situation="PRE_LAUNCH", crew_count=0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.FLKO_LAUNCH)
        self.assertIn("crew", state.flake_reason)
        self.assertIn("UNCREWED", state.flake_reason)

    def test_min_crew_zero_disables_the_gate(self):
        params = _flko_params(min_crew=0)
        state = mlib.forge_lko_initial_state(params)
        state, _ = mlib.forge_lko_decide(state, snap(ut=0.0))
        state, _ = mlib.forge_lko_decide(state, snap(ut=5.0, situation="PRE_LAUNCH"))
        state, actions = mlib.forge_lko_decide(
            state, snap(ut=10.0, situation="PRE_LAUNCH"))
        self.assertEqual(state.phase, mlib.FLKO_ASCENT)
        self.assertIn(mlib.ACTION_MJ_ENGAGE_ASCENT, [a.kind for a in actions])

    def test_ascent_completion_executes_nodes_with_autowarp_action(self):
        # ACTION_MJ_EXECUTE_NODES (not the bare circularization action) is what
        # sets node_executor autowarp explicitly (B-DOCK flight-12 lesson).
        state, per_frame = _flko_to_park()
        kinds = [a.kind for frame in per_frame for a in frame]
        self.assertIn(mlib.ACTION_MJ_EXECUTE_NODES, kinds)

    def test_happy_path_reaches_park_with_the_park_contract_actions(self):
        state, per_frame = _flko_to_park()
        self.assertEqual(state.phase, mlib.FLKO_PARK)
        self.assertTrue(state.split_ever_confirmed)
        self.assertTrue(state.ignition_ever_confirmed)
        park_actions = [a.kind for a in per_frame[-1]]
        # Throttle cut + nodes cleared + attitude held: the SAVED configuration.
        self.assertEqual(park_actions, [mlib.ACTION_CUT_THROTTLE,
                                        mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES,
                                        mlib.ACTION_SET_SAS,
                                        mlib.ACTION_SET_RCS])

    def test_park_requires_debounce_and_dwell(self):
        state, _ = _flko_to_park()
        entry = state.phase_entry_ut
        # Stable immediately, but the dwell has not elapsed -> still PARK.
        state, _ = mlib.forge_lko_decide(state, _flko_parked(entry + 1.0))
        state, _ = mlib.forge_lko_decide(state, _flko_parked(entry + 2.0))
        self.assertEqual(state.phase, mlib.FLKO_PARK)
        self.assertFalse(state.done)
        # Past the 60 s dwell with the streak held -> ORBIT (done, MISSION-OK).
        state, _ = mlib.forge_lko_decide(state, _flko_parked(entry + 61.0))
        self.assertEqual(state.phase, mlib.FLKO_ORBIT)
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict)

    def test_park_rejects_a_tumbling_or_low_or_unread_orbit(self):
        for kw in ({"angular_velocity": 0.5},          # tumbling
                   {"angular_velocity": float("nan")},  # unread -> fail closed
                   {"periapsis": 60000.0},              # inside the atmosphere
                   {"apoapsis": 400000.0},              # outside the tolerance
                   {"situation": "FLYING"}):            # not a park situation
            state, _ = _flko_to_park()
            entry = state.phase_entry_ut
            state, _ = mlib.forge_lko_decide(state, _flko_parked(entry + 1.0, **kw))
            state, _ = mlib.forge_lko_decide(state, _flko_parked(entry + 61.0, **kw))
            self.assertEqual(state.park_stable_streak, 0, kw)
            self.assertEqual(state.phase, mlib.FLKO_PARK, kw)

    def test_park_timeout_flakes_with_a_named_reason(self):
        state, _ = _flko_to_park()
        entry = state.phase_entry_ut
        state, _ = mlib.forge_lko_decide(
            state, _flko_parked(entry + 1000.0, situation="FLYING"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.FLKO_PARK)
        self.assertIn("never reached a stable park", state.flake_reason)

    def test_separation_without_split_flakes_named(self):
        state = mlib.forge_lko_initial_state(FLKO_PARAMS)
        frames = [
            snap(ut=0.0, situation="FLYING"),
            snap(ut=5.0, situation="PRE_LAUNCH", crew_count=2),
            snap(ut=10.0, situation="PRE_LAUNCH", crew_count=2),
            snap(ut=300.0, apoapsis=99000.0, mj_ascent_complete=True),
            snap(ut=400.0, periapsis=99000.0, vessel_count=1),   # -> SEPARATE
            snap(ut=600.0, vessel_count=1),                      # no split, past budget
        ]
        state, _ = drive_flko(state, frames)
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no separation observed", state.flake_reason)

    def test_separation_caps_activations_at_two(self):
        # HARD CAP: at most TWO stage activations from the circularize->SEPARATE
        # transition onward (drop the spent core, then ignite the orbital stage).
        # A THIRD would fire the istg=0 heat-shield decoupler. A split that never
        # ignites must flake, NOT keep staging the craft apart.
        state = mlib.forge_lko_initial_state(FLKO_PARAMS)
        frames = [
            snap(ut=0.0, situation="FLYING"),
            snap(ut=5.0, situation="PRE_LAUNCH", crew_count=2),
            snap(ut=10.0, situation="PRE_LAUNCH", crew_count=2),
            snap(ut=300.0, apoapsis=99000.0, mj_ascent_complete=True),
            snap(ut=400.0, periapsis=99000.0, vessel_count=1),   # SEPARATE entry
        ] + [snap(ut=401.0 + i, vessel_count=2, available_thrust=0.0)
             for i in range(20)
             ] + [snap(ut=600.0, vessel_count=2, available_thrust=0.0)]
        state, per_frame = drive_flko(state, frames)
        # Frame 3 (the ascent entry) carries the LAUNCH ignition activation, which
        # is not part of the separation cap; frames 4.. are the separation.
        launch_stages = [a for frame in per_frame[:4] for a in frame
                         if a.kind == mlib.ACTION_ACTIVATE_STAGE]
        sep_stages = [a for frame in per_frame[4:] for a in frame
                      if a.kind == mlib.ACTION_ACTIVATE_STAGE]
        self.assertEqual(len(launch_stages), 1)
        self.assertEqual(len(sep_stages), 2)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no ignition", state.flake_reason)

    def test_vessel_lost_during_launch_is_transient_not_terminal(self):
        state = mlib.forge_lko_initial_state(FLKO_PARAMS)
        state, _ = mlib.forge_lko_decide(state, snap(ut=0.0))
        state, _ = mlib.forge_lko_decide(state, snap(ut=2.0, vessel_lost=True))
        self.assertFalse(state.done)
        self.assertEqual(state.phase, mlib.FLKO_LAUNCH)

    def test_vessel_lost_in_flight_is_terminal(self):
        state, _ = _flko_to_park()
        state, _ = mlib.forge_lko_decide(state, snap(ut=500.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", state.loss_reason)

    def test_params_from_dict_parses_the_spec_block(self):
        p = mlib.forge_lko_params_from_dict({
            "craftName": "Kerbal X",
            "launchSite": "LaunchPad",
            "crewNames": ["Valentina Kerman", "Bob Kerman"],
            "minCrew": 2,
            "targetApoapsisMeters": 100000,
            "targetPeriapsisMeters": 100000,
            "apoErrorMeters": 10000,
            "periErrorMeters": 10000,
            "ascentTimeoutSeconds": 900,
            "circularizeTimeoutSeconds": 2400,
            "separationTimeoutSeconds": 120,
            "parkSituations": ["ORBITING"],
            "parkDwellSeconds": 60,
            "parkTimeoutSeconds": 600,
            "minSafePeriapsisMeters": 75000,
        })
        self.assertEqual(p.crew_names, ("Valentina Kerman", "Bob Kerman"))
        self.assertEqual(p.min_crew, 2)
        self.assertEqual(p.park_situations, ("ORBITING",))
        self.assertEqual(p.circularize_timeout, 2400.0)
        # Omitted crewNames -> None (KSP default manifest), never an empty tuple.
        self.assertIsNone(mlib.forge_lko_params_from_dict({}).crew_names)

    def test_b2_projection_carries_the_orbit_tolerances(self):
        b2 = mlib.forge_lko_b2_params(FLKO_PARAMS)
        self.assertEqual(b2.target_apoapsis, 100000.0)
        self.assertEqual(b2.peri_error, 10000.0)
        self.assertEqual(b2.eccentricity_max, FLKO_PARAMS.eccentricity_max)


class ForgeLkoAssertionTests(unittest.TestCase):
    def _good_state(self):
        state, _ = _flko_to_park()
        entry = state.phase_entry_ut
        state, _ = mlib.forge_lko_decide(state, _flko_parked(entry + 1.0))
        state, _ = mlib.forge_lko_decide(state, _flko_parked(entry + 2.0))
        state, _ = mlib.forge_lko_decide(state, _flko_parked(entry + 61.0))
        return state

    def _good_frames(self):
        return [snap(situation="ORBITING", apoapsis=100000.0, periapsis=99500.0,
                     eccentricity=0.001, inclination=0.1, crew_count=2)
                for _ in range(5)]

    def test_all_rows_met_on_a_clean_forge(self):
        state = self._good_state()
        outs = mlib.evaluate_forge_lko_assertions(
            self._good_frames(), FLKO_PARAMS,
            phases_reached=state.phases_reached, state=state)
        names = [o.name for o in outs]
        self.assertEqual(names[:4], ["launched", "crewAboard", "separated",
                                     "parkedStable"])
        # The B2 orbit rows ride verbatim after the forge-specific ones.
        self.assertEqual(names[4:], ["apoapsisError", "periapsisError",
                                     "eccentricity", "inclinationError"])
        self.assertTrue(all(o.met for o in outs), [(o.name, o.value) for o in outs])

    def test_uncrewed_stamp_is_unmet(self):
        state = self._good_state()
        frames = [snap(situation="ORBITING", apoapsis=100000.0, periapsis=99500.0,
                       eccentricity=0.001, inclination=0.1)  # crew_count -1 unread
                  for _ in range(5)]
        outs = {o.name: o for o in mlib.evaluate_forge_lko_assertions(
            frames, FLKO_PARAMS, phases_reached=state.phases_reached, state=state)}
        self.assertFalse(outs["crewAboard"].met)
        self.assertIsNone(outs["crewAboard"].value)

    def test_separated_unmet_without_the_ignition_evidence(self):
        state = self._good_state()
        state = state.__class__(**{**state.__dict__,
                                   "ignition_ever_confirmed": False})
        outs = {o.name: o for o in mlib.evaluate_forge_lko_assertions(
            self._good_frames(), FLKO_PARAMS,
            phases_reached=state.phases_reached, state=state)}
        self.assertFalse(outs["separated"].met)

    def test_parked_unmet_when_the_final_situation_is_not_a_park(self):
        state = self._good_state()
        frames = self._good_frames() + [snap(situation="FLYING", crew_count=2)]
        outs = {o.name: o for o in mlib.evaluate_forge_lko_assertions(
            frames, FLKO_PARAMS, phases_reached=state.phases_reached, state=state)}
        self.assertFalse(outs["parkedStable"].met)


# ===========================================================================
# B-DOCK machine + assertions.
# ===========================================================================


BDOCK_PARAMS = mlib.BDockParams(
    station_apoapsis=110000.0,
    station_periapsis=110000.0,
    interceptor_apoapsis=90000.0,
    interceptor_periapsis=90000.0,
    apo_error=5000.0,
    peri_error=5000.0,
    ascent_timeout=1200.0,
    circularize_timeout=600.0,
    craft_name="Kerbal X",
    launch_settle_debounce=2,
    launch_timeout=300.0,
    approach_distance=100.0,
    max_phasing_orbits=5.0,
    match_speed=1.0,
    dock_speed=0.5,
    transfer_amount_lf=40.0,
    transfer_amount_mp=15.0,
    station_commit_timeout=300.0,
    rendezvous_timeout=30000.0,
    dock_timeout=600.0,
    transfer_timeout=120.0,
    undock_timeout=120.0,
    rendezvous_noprogress_frames=5,
)


def _bdock(**overrides):
    return mlib.bdock_initial_state(mlib.replace(BDOCK_PARAMS, **overrides)
                                    if overrides else BDOCK_PARAMS)


def _bdock_walk_to(phase, params=BDOCK_PARAMS):
    """Drive a fresh B-DOCK machine to the given phase over a scripted happy path,
    returning (state, per_frame_actions_up_to_entry). Only phases up to and INTO
    ``phase`` are walked; used so a per-phase test starts from a realistic state."""
    state = mlib.bdock_initial_state(params)
    frames = [
        snap(ut=0.0),                                              # PRELAUNCH->STATION-ASCENT
        snap(ut=100.0, apoapsis=108000.0, mj_ascent_complete=True),  # ->STATION-CIRCULARIZE
        snap(ut=150.0, periapsis=109000.0, vessel_count=1),       # ->STATION-SEPARATE (baseline=1, drop core)
        snap(ut=151.0, vessel_count=2, available_thrust=0.0),     # split settle 1 (engine unlit)
        snap(ut=152.0, vessel_count=2, available_thrust=0.0),     # split settle 2
        snap(ut=153.0, vessel_count=2, available_thrust=0.0),     # split settle 3 -> confirmed, ignite
        snap(ut=154.0, vessel_count=2, available_thrust=200000.0),   # thrust settle 1
        snap(ut=155.0, vessel_count=2, available_thrust=200000.0),   # thrust settle 2
        snap(ut=156.0, vessel_count=2, available_thrust=200000.0),   # thrust settle 3 -> STATION-ORBIT
        snap(ut=160.0),                                           # ->STATION-COMMIT (capture+commit)
        snap(ut=161.0, seam_commit_result="OK"),                 # ->INT-LAUNCH
        snap(ut=170.0, situation="PRE_LAUNCH"),                  # settle 1
        snap(ut=175.0, situation="PRE_LAUNCH"),                  # settle 2 -> INT-ASCENT
        snap(ut=400.0, apoapsis=88000.0, mj_ascent_complete=True),  # ->INT-CIRCULARIZE
        snap(ut=450.0, periapsis=89000.0, vessel_count=2),       # ->INT-SEPARATE (baseline=2, drop core)
        snap(ut=451.0, vessel_count=3, available_thrust=0.0),    # split settle 1 (engine unlit)
        snap(ut=452.0, vessel_count=3, available_thrust=0.0),    # split settle 2
        snap(ut=453.0, vessel_count=3, available_thrust=0.0),    # split settle 3 -> confirmed, ignite
        snap(ut=454.0, vessel_count=3, available_thrust=180000.0),   # thrust settle 1
        snap(ut=455.0, vessel_count=3, available_thrust=180000.0),   # thrust settle 2
        snap(ut=456.0, vessel_count=3, available_thrust=180000.0),   # thrust settle 3 -> INT-PHASING-ORBIT
        snap(ut=460.0),                                          # ->SET-TARGET
        snap(ut=470.0, target_set=True),                        # ->RENDEZVOUS
        snap(ut=480.0, mj_rendezvous_enabled=True, target_distance=5000.0),  # AP running
        snap(ut=490.0, mj_rendezvous_enabled=False, target_distance=80.0),   # ->MATCH-VELOCITY
        snap(ut=500.0, target_rel_speed=0.5),                   # ->DOCK (entry: abort+set-target, enable pending)
        snap(ut=505.0, target_distance=90.0),                   # deferred enable -> MJ_ENABLE_DOCKING
        snap(ut=510.0, mj_docking_enabled=True, docking_state="Docking", target_distance=50.0),  # AP running
        snap(ut=520.0, mj_docking_enabled=False, docking_state="Docked"),    # ->TRANSFER (T1)
        snap(ut=525.0, transfer_complete=True, vessel_count=3),  # T1 done -> T2
        snap(ut=530.0, transfer_complete=True, vessel_count=3),  # T2 done -> UNDOCK (baseline=3)
        snap(ut=540.0, vessel_count=4, docking_state="Ready"),   # split -> TERMINAL
    ]
    reached = state
    per = []
    for f in frames:
        reached, actions = mlib.bdock_decide(reached, f)
        per.append((reached.phase, actions))
        if reached.phase == phase or reached.done:
            break
    return reached, per


class BDockHappyPathTests(unittest.TestCase):
    """The full two-vessel walk: Station ascent+commit, Interceptor launch+ascent,
    rendezvous, dock, two transfers, undock, terminal."""

    def test_full_happy_path_reaches_terminal(self):
        state, per = _bdock_walk_to(mlib.BDOCK_TERMINAL)
        self.assertTrue(state.done)
        self.assertEqual(state.phase, mlib.BDOCK_TERMINAL)
        self.assertIsNone(state.verdict)
        self.assertTrue(state.docked_confirmed)
        self.assertEqual(state.transfers_done, 2)
        self.assertTrue(state.undock_confirmed)
        # Every phase visited in order, INCLUDING the two stage-separation phases.
        for want in (mlib.BDOCK_STATION_CIRCULARIZE, mlib.BDOCK_STATION_SEPARATE,
                     mlib.BDOCK_STATION_ORBIT, mlib.BDOCK_STATION_COMMIT,
                     mlib.BDOCK_INT_LAUNCH, mlib.BDOCK_INT_CIRCULARIZE,
                     mlib.BDOCK_INT_SEPARATE, mlib.BDOCK_INT_PHASING_ORBIT,
                     mlib.BDOCK_RENDEZVOUS, mlib.BDOCK_DOCK, mlib.BDOCK_TRANSFER,
                     mlib.BDOCK_UNDOCK, mlib.BDOCK_TERMINAL):
            self.assertIn(want, state.phases_reached)
        # SEPARATE precedes its park in phases_reached order.
        pr = list(state.phases_reached)
        self.assertLess(pr.index(mlib.BDOCK_STATION_SEPARATE),
                        pr.index(mlib.BDOCK_STATION_ORBIT))
        self.assertLess(pr.index(mlib.BDOCK_INT_SEPARATE),
                        pr.index(mlib.BDOCK_INT_PHASING_ORBIT))

    def test_prelaunch_emits_station_ascent_at_station_apoapsis(self):
        state = _bdock()
        new, actions = mlib.bdock_decide(state, snap(ut=0.0))
        self.assertEqual(new.phase, mlib.BDOCK_STATION_ASCENT)
        self.assertEqual(actions[0], Action(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, 110000.0))
        self.assertIn(Action(mlib.ACTION_ACTIVATE_STAGE), actions)

    def test_station_orbit_captures_and_commits(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_STATION_COMMIT)
        # The entry to STATION-COMMIT emits capture + commit.
        state2 = mlib.bdock_initial_state(BDOCK_PARAMS)
        # Drive through STATION-SEPARATE (drop core + ignite) to STATION-ORBIT,
        # then step into STATION-COMMIT to read the actions.
        for f in (snap(ut=0.0),
                  snap(ut=100.0, apoapsis=108000.0, mj_ascent_complete=True),
                  snap(ut=150.0, periapsis=109000.0, vessel_count=1),  # ->STATION-SEPARATE
                  snap(ut=151.0, vessel_count=2, available_thrust=0.0),  # split settle 1
                  snap(ut=152.0, vessel_count=2, available_thrust=0.0),  # split settle 2
                  snap(ut=153.0, vessel_count=2, available_thrust=0.0),  # split settle 3 -> ignite
                  snap(ut=154.0, vessel_count=2, available_thrust=2e5),  # thrust settle 1
                  snap(ut=155.0, vessel_count=2, available_thrust=2e5),  # thrust settle 2
                  snap(ut=156.0, vessel_count=2, available_thrust=2e5)): # thrust settle 3 -> STATION-ORBIT
            state2, actions = mlib.bdock_decide(state2, f)
        self.assertEqual(state2.phase, mlib.BDOCK_STATION_ORBIT)
        state2, actions = mlib.bdock_decide(state2, snap(ut=160.0))
        self.assertEqual(state2.phase, mlib.BDOCK_STATION_COMMIT)
        self.assertEqual(actions, [Action(mlib.ACTION_CAPTURE_STATION),
                                   Action(mlib.ACTION_PARSEK_COMMIT_TREE)])

    def test_int_launch_emits_launch_vessel(self):
        # From STATION-COMMIT, an OK seam result launches the Interceptor.
        state, _ = _bdock_walk_to(mlib.BDOCK_STATION_COMMIT)
        state, actions = mlib.bdock_decide(state, snap(ut=161.0, seam_commit_result="OK"))
        self.assertEqual(state.phase, mlib.BDOCK_INT_LAUNCH)
        self.assertEqual(actions, [Action(mlib.ACTION_LAUNCH_VESSEL, text="Kerbal X")])

    def test_station_commit_error_flakes_with_named_reason(self):
        # Review follow-up 5: a seam ERROR/TIMEOUT flake names the seam outcome
        # (not the generic phase-timeout wording).
        for result in ("ERROR", "TIMEOUT"):
            state, _ = _bdock_walk_to(mlib.BDOCK_STATION_COMMIT)
            state, _ = mlib.bdock_decide(
                state, snap(ut=161.0, seam_commit_result=result))
            self.assertTrue(state.done)
            self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
            self.assertIsNotNone(state.flake_reason)
            self.assertIn("tree-commit seam returned %s" % result,
                          state.flake_reason)
            self.assertIn(mlib.BDOCK_STATION_COMMIT, state.flake_reason)


class BDockSeparateTests(unittest.TestCase):
    """Post-circularize two-step stage separation (flight-3 + flight-4 lessons):
    STATION-CIRCULARIZE -> STATION-SEPARATE emits ONE entry ACTIVATE_STAGE (drop
    core), confirms the split on a debounced vessel_count bump, then IGNITES the
    orbital engine (one more activation UNLESS thrust is already up), and completes
    on debounced available_thrust > 0. HARD CAP: 2 activations. INT-SEPARATE
    mirrors it. Give-up distinguishes no-split from split-but-no-ignition."""

    K = mlib.BDOCK_SEPARATION_DEBOUNCE

    def _at_station_separate(self, entry_count=1):
        # Drive to STATION-CIRCULARIZE, then complete circularize to enter
        # STATION-SEPARATE (baseline captured from the entry frame's count).
        state, _ = _bdock_walk_to(mlib.BDOCK_STATION_CIRCULARIZE)
        state, actions = mlib.bdock_decide(
            state, snap(ut=150.0, periapsis=109000.0, vessel_count=entry_count))
        return state, actions

    def _confirm_split(self, state, base=1, start_ut=151.0):
        # Feed K vessel-count-bump frames (thrust still 0) to confirm the split;
        # the K-th frame emits the ignition activation. Returns (state, actions_on
        # _the_ignition_frame).
        actions = []
        for i in range(self.K):
            state, actions = mlib.bdock_decide(
                state, snap(ut=start_ut + i, vessel_count=base + 1,
                            available_thrust=0.0))
        return state, actions

    def test_circularize_enters_separate_and_activates_stage_once(self):
        state, actions = self._at_station_separate(entry_count=1)
        self.assertEqual(state.phase, mlib.BDOCK_STATION_SEPARATE)
        # EXACTLY ONE stage activation on entry (drop the spent core).
        self.assertEqual(actions, [Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(state.separate_baseline_vessel_count, 1)
        self.assertEqual(state.separate_activations, 1)
        # A subsequent split-settle frame (before confirm) does NOT re-activate.
        state, actions2 = mlib.bdock_decide(
            state, snap(ut=151.0, vessel_count=2, available_thrust=0.0))
        self.assertEqual(state.phase, mlib.BDOCK_STATION_SEPARATE)
        self.assertEqual(actions2, [])
        self.assertEqual(state.separate_activations, 1)

    def test_split_then_ignition_then_completes(self):
        # Realistic Kerbal X: core drops (thrust 0), then the ignition activation
        # lights the orbital LV-T45 -> thrust up -> complete.
        state, _ = self._at_station_separate(entry_count=1)
        state, ign_actions = self._confirm_split(state, base=1)
        # The split-confirming frame emits EXACTLY ONE ignition activation.
        self.assertTrue(state.separate_split_confirmed)
        self.assertEqual(ign_actions, [Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(state.separate_activations, 2)
        self.assertEqual(state.phase, mlib.BDOCK_STATION_SEPARATE)
        # Two thrust frames are not enough; the K-th completes -> STATION-ORBIT.
        for i in range(self.K - 1):
            state, a = mlib.bdock_decide(
                state, snap(ut=160.0 + i, vessel_count=2, available_thrust=2e5))
            self.assertEqual(state.phase, mlib.BDOCK_STATION_SEPARATE)
            self.assertEqual(a, [])
        state, _ = mlib.bdock_decide(
            state, snap(ut=170.0, vessel_count=2, available_thrust=2e5))
        self.assertEqual(state.phase, mlib.BDOCK_STATION_ORBIT)
        self.assertEqual(state.separate_activations, 0)  # reset on completion

    def test_thrust_already_up_skips_second_activation(self):
        # A craft whose decoupler + engine share a stage: thrust is up throughout
        # the split debounce, so the split confirms with thrust ALREADY debounced
        # and the phase completes with NO ignition activation (only 1 total).
        state, _ = self._at_station_separate(entry_count=1)
        actions_seen = []
        for i in range(self.K):
            state, a = mlib.bdock_decide(
                state, snap(ut=151.0 + i, vessel_count=2, available_thrust=2e5))
            actions_seen.append(a)
        self.assertEqual(state.phase, mlib.BDOCK_STATION_ORBIT)
        # Never a second activation: exactly the one entry activation happened.
        for a in actions_seen:
            self.assertNotIn(Action(mlib.ACTION_ACTIVATE_STAGE), a)
        # The completing frame emits the attitude hold (SAS + RCS) into the next
        # phase (flight-10 tumble fix); every earlier frame emits nothing.
        self.assertEqual(actions_seen[-1], [
            Action(mlib.ACTION_SET_SAS), Action(mlib.ACTION_SET_RCS, value=1.0)])
        for a in actions_seen[:-1]:
            self.assertEqual(a, [])
        # separate_activations was 1 (entry) and reset to 0 on completion.
        self.assertEqual(state.separate_activations, 0)

    def test_activations_hard_capped_at_two(self):
        # Split confirms but thrust never comes up: the ignition activation fires
        # once, then NEVER a third (which would drop the heat shield).
        state, _ = self._at_station_separate(entry_count=1)
        state, _ = self._confirm_split(state, base=1)
        self.assertEqual(state.separate_activations, 2)
        activate = Action(mlib.ACTION_ACTIVATE_STAGE)
        for i in range(20):
            state, a = mlib.bdock_decide(
                state, snap(ut=160.0 + i, vessel_count=2, available_thrust=0.0))
            if state.done:
                break
            self.assertNotIn(activate, a)  # cap 2 -> never a third
        self.assertEqual(state.separate_activations, 2)

    def test_int_separate_two_step_completes_to_phasing_orbit(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_INT_CIRCULARIZE)
        state, actions = mlib.bdock_decide(
            state, snap(ut=450.0, periapsis=89000.0, vessel_count=2))
        self.assertEqual(state.phase, mlib.BDOCK_INT_SEPARATE)
        self.assertEqual(actions, [Action(mlib.ACTION_ACTIVATE_STAGE)])
        self.assertEqual(state.separate_baseline_vessel_count, 2)
        # Confirm split (thrust 0) then ignite + thrust up -> INT-PHASING-ORBIT.
        for i in range(self.K):
            state, _ = mlib.bdock_decide(
                state, snap(ut=451.0 + i, vessel_count=3, available_thrust=0.0))
        for i in range(self.K):
            state, _ = mlib.bdock_decide(
                state, snap(ut=460.0 + i, vessel_count=3, available_thrust=1.8e5))
        self.assertEqual(state.phase, mlib.BDOCK_INT_PHASING_ORBIT)

    def test_no_separation_within_budget_flakes_with_named_reason(self):
        state, _ = self._at_station_separate(entry_count=1)
        # No bump ever; the SEPARATE budget (separation_timeout=120) elapses.
        state, actions = mlib.bdock_decide(
            state, snap(ut=150.0 + 121.0, vessel_count=1))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.BDOCK_STATION_SEPARATE)
        verdict, reason = mlib.resolve_flight_verdict(state, [])
        self.assertEqual(verdict, mlib.MISSION_FLAKE)
        self.assertIn("STATION-SEPARATE", reason)
        self.assertIn("no separation observed", reason)
        self.assertEqual(actions, [])

    def test_split_but_no_ignition_flakes_with_distinct_reason(self):
        # Split confirmed, ignition activation fired, but thrust stays 0 past the
        # budget -> a DISTINCT give-up reason (not the no-split one).
        state, _ = self._at_station_separate(entry_count=1)
        state, _ = self._confirm_split(state, base=1)
        self.assertTrue(state.separate_split_confirmed)
        state, actions = mlib.bdock_decide(
            state, snap(ut=150.0 + 121.0, vessel_count=2, available_thrust=0.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        verdict, reason = mlib.resolve_flight_verdict(state, [])
        self.assertEqual(verdict, mlib.MISSION_FLAKE)
        self.assertIn("separated but no ignition", reason)
        self.assertNotIn("no separation observed", reason)

    def test_vessel_count_stuck_at_baseline_does_not_complete(self):
        state, _ = self._at_station_separate(entry_count=1)
        # vessel_count never exceeds the baseline: never confirms the split, never
        # ignites, never advances.
        for ut in (152.0, 154.0, 156.0, 158.0):
            state, _ = mlib.bdock_decide(
                state, snap(ut=ut, vessel_count=1, available_thrust=0.0))
            self.assertEqual(state.phase, mlib.BDOCK_STATION_SEPARATE)
            self.assertFalse(state.separate_split_confirmed)
            self.assertEqual(state.separate_settle_streak, 0)
        self.assertFalse(state.done)

    def test_nan_thrust_never_completes_fail_closed(self):
        # Fail-closed: after the split, an unread (NaN) available_thrust is never
        # treated as ignited, so the phase does not complete on it.
        state, _ = self._at_station_separate(entry_count=1)
        state, _ = self._confirm_split(state, base=1)
        for i in range(self.K + 2):
            state, _ = mlib.bdock_decide(
                state, snap(ut=160.0 + i, vessel_count=2))  # available_thrust NaN
            self.assertEqual(state.phase, mlib.BDOCK_STATION_SEPARATE)
        self.assertFalse(state.done)


class BDockSeamCommitTests(unittest.TestCase):
    """The mid-mission command-seam commit (route 1): OK advances, ERROR/TIMEOUT
    flakes, "" waits (bounded)."""

    def _at_commit(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_STATION_COMMIT)
        return state

    def test_ok_advances_to_int_launch(self):
        state = self._at_commit()
        state, _ = mlib.bdock_decide(state, snap(ut=162.0, seam_commit_result="OK"))
        self.assertEqual(state.phase, mlib.BDOCK_INT_LAUNCH)

    def test_error_flakes(self):
        state = self._at_commit()
        state, _ = mlib.bdock_decide(state, snap(ut=162.0, seam_commit_result="ERROR"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.BDOCK_STATION_COMMIT)

    def test_timeout_flakes(self):
        state = self._at_commit()
        state, _ = mlib.bdock_decide(state, snap(ut=162.0, seam_commit_result="TIMEOUT"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)

    def test_empty_result_waits_then_budget_flakes(self):
        state = self._at_commit()
        # "" keeps waiting...
        state, _ = mlib.bdock_decide(state, snap(ut=200.0, seam_commit_result=""))
        self.assertEqual(state.phase, mlib.BDOCK_STATION_COMMIT)
        # ...until the station_commit_timeout (300 s) elapses -> FLAKE.
        state, _ = mlib.bdock_decide(state, snap(ut=500.0, seam_commit_result=""))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)


class BDockRendezvousTests(unittest.TestCase):
    def _at_rendezvous(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_RENDEZVOUS)
        return state

    def test_latch_and_distance_advance_to_match_velocity(self):
        state = self._at_rendezvous()
        state, _ = mlib.bdock_decide(state, snap(
            ut=480.0, mj_rendezvous_enabled=True, target_distance=5000.0))
        self.assertTrue(state.rendezvous_ever_enabled)
        # Latch flips off + within approach distance -> MATCH-VELOCITY.
        state, actions = mlib.bdock_decide(state, snap(
            ut=490.0, mj_rendezvous_enabled=False, target_distance=80.0))
        self.assertEqual(state.phase, mlib.BDOCK_MATCH_VELOCITY)
        self.assertEqual(actions, [Action(mlib.ACTION_MJ_KILL_REL_VEL)])

    def test_latch_off_but_far_does_not_advance(self):
        # NIT-15: the AP disabling while still FAR is not a rendezvous completion.
        state = self._at_rendezvous()
        state, _ = mlib.bdock_decide(state, snap(
            ut=480.0, mj_rendezvous_enabled=True, target_distance=5000.0))
        state, _ = mlib.bdock_decide(state, snap(
            ut=490.0, mj_rendezvous_enabled=False, target_distance=800.0))
        self.assertEqual(state.phase, mlib.BDOCK_RENDEZVOUS)

    def test_nan_distance_fails_closed(self):
        state = self._at_rendezvous()
        state, _ = mlib.bdock_decide(state, snap(
            ut=480.0, mj_rendezvous_enabled=True, target_distance=5000.0))
        state, _ = mlib.bdock_decide(state, snap(
            ut=490.0, mj_rendezvous_enabled=False, target_distance=float("nan")))
        self.assertEqual(state.phase, mlib.BDOCK_RENDEZVOUS)

    def test_no_progress_gives_up(self):
        # Distance never beats the running minimum for rendezvous_noprogress_frames
        # (5) consecutive frames -> FLAKE.
        state = self._at_rendezvous()
        state, _ = mlib.bdock_decide(state, snap(
            ut=475.0, mj_rendezvous_enabled=True, target_distance=3000.0))  # min=3000
        for i in range(5):
            state, _ = mlib.bdock_decide(state, snap(
                ut=476.0 + i, mj_rendezvous_enabled=True, target_distance=3000.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.BDOCK_RENDEZVOUS)

    def test_no_progress_paused_while_node_pending(self):
        # Review follow-up 1 (flight-11): the no-progress detector is PAUSED
        # (counter reset each poll) while a maneuver node is pending -- the
        # rendezvous AP legitimately waits minutes for a burn window with the
        # distance flat. A flat distance WITH node_count > 0 must NEVER flake, even
        # across far more than rendezvous_noprogress_frames (5) polls; the phase
        # budget bounds slow-but-alive, not this watchdog.
        state = self._at_rendezvous()
        # altitude varies each poll (a live orbiting vessel) so the shared
        # frozen-telemetry detector never trips; only target_distance is flat.
        state, _ = mlib.bdock_decide(state, snap(
            ut=475.0, altitude=100000.0, mj_rendezvous_enabled=True,
            target_distance=3000.0, node_count=1))  # min=3000, node pending
        for i in range(5 * 4):  # 20 polls >> rendezvous_noprogress_frames=5
            state, _ = mlib.bdock_decide(state, snap(
                ut=476.0 + i, altitude=100000.0 + i, mj_rendezvous_enabled=True,
                target_distance=3000.0, node_count=1))
            self.assertFalse(state.done, "flaked at poll %d with a node pending" % i)
        self.assertEqual(state.phase, mlib.BDOCK_RENDEZVOUS)
        self.assertEqual(state.rendezvous_noprogress_count, 0)

    def test_no_progress_flakes_after_node_consumed(self):
        # Review follow-up 1: once the node is CONSUMED (node_count back to 0), the
        # no-progress watchdog resumes -- a flat distance then flakes at exactly
        # rendezvous_noprogress_frames (5) consecutive non-improving polls.
        state = self._at_rendezvous()
        # altitude varies each poll (a live vessel) so only the no-progress
        # watchdog can flake, never the frozen-telemetry detector.
        # Node pending, distance flat: counter stays 0 (paused).
        state, _ = mlib.bdock_decide(state, snap(
            ut=475.0, altitude=100000.0, mj_rendezvous_enabled=True,
            target_distance=3000.0, node_count=1))
        self.assertEqual(state.rendezvous_noprogress_count, 0)
        # Node consumed; establish the running minimum on the first no-node poll.
        state, _ = mlib.bdock_decide(state, snap(
            ut=476.0, altitude=100001.0, mj_rendezvous_enabled=True,
            target_distance=3000.0,
            node_count=0))  # min=3000, count=0 (first flat, not yet non-improving)
        self.assertFalse(state.done)
        # Five consecutive non-improving no-node polls -> FLAKE at the threshold.
        for i in range(5):
            self.assertFalse(state.done)
            state, _ = mlib.bdock_decide(state, snap(
                ut=477.0 + i, altitude=100002.0 + i, mj_rendezvous_enabled=True,
                target_distance=3000.0, node_count=0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.BDOCK_RENDEZVOUS)

    def test_rendezvous_budget_flakes(self):
        state = self._at_rendezvous()
        state, _ = mlib.bdock_decide(state, snap(
            ut=470.0 + 40000.0, mj_rendezvous_enabled=True, target_distance=5000.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)


class BDockMatchVelocityTests(unittest.TestCase):
    """MATCH-VELOCITY (flight-5): a bounded give-up (matchTimeoutSeconds), NaN
    rel-speed fail-closed, and a one-shot dropped-target re-acquire."""

    def _at_match(self):
        # Reach MATCH-VELOCITY (entered at ut 490 in the walk).
        state, _ = _bdock_walk_to(mlib.BDOCK_MATCH_VELOCITY)
        self.assertEqual(state.phase, mlib.BDOCK_MATCH_VELOCITY)
        return state

    def test_finite_rel_speed_below_floor_advances_to_dock(self):
        state = self._at_match()
        state, actions = mlib.bdock_decide(state, snap(ut=495.0, target_rel_speed=0.5))
        self.assertEqual(state.phase, mlib.BDOCK_DOCK)
        # Flight-9 stagger: entry abort + set-target only; the enable is deferred.
        self.assertIn(Action(mlib.ACTION_MJ_ABORT_NODE_EXEC), actions)
        self.assertIn(Action(mlib.ACTION_SET_TARGET_DOCKING_PORT), actions)
        self.assertNotIn(Action(mlib.ACTION_MJ_ENABLE_DOCKING, value=0.5), actions)
        self.assertTrue(state.dock_enable_pending)

    def test_rel_speed_above_floor_stays(self):
        state = self._at_match()
        state, actions = mlib.bdock_decide(state, snap(ut=495.0, target_rel_speed=8.0))
        self.assertEqual(state.phase, mlib.BDOCK_MATCH_VELOCITY)
        self.assertEqual(actions, [])

    def test_budget_flakes_with_named_reason(self):
        state = self._at_match()
        # Rel-speed stuck above the floor past matchTimeoutSeconds (600) -> FLAKE.
        state, actions = mlib.bdock_decide(
            state, snap(ut=490.0 + 601.0, target_rel_speed=8.5))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.BDOCK_MATCH_VELOCITY)
        verdict, reason = mlib.resolve_flight_verdict(state, [])
        self.assertEqual(verdict, mlib.MISSION_FLAKE)
        self.assertIn("MATCH-VELOCITY", reason)
        self.assertIn("did not reach rel-speed floor", reason)
        self.assertIn("8.5", reason)  # the last value rides the reason
        self.assertEqual(actions, [])

    def test_nan_rel_speed_never_completes_fail_closed(self):
        state = self._at_match()
        # NaN rel-speed for a couple frames must not complete (below-floor NaN
        # would falsely satisfy <= match_speed without the finite guard).
        for i in range(2):
            state, _ = mlib.bdock_decide(
                state, snap(ut=491.0 + i, target_rel_speed=float("nan")))
            self.assertEqual(state.phase, mlib.BDOCK_MATCH_VELOCITY)
        self.assertFalse(state.done)

    def test_dropped_target_reacquired_exactly_once(self):
        state = self._at_match()
        # K consecutive NaN frames -> one SET_TARGET re-acquire, latch set.
        actions_seen = []
        for i in range(mlib.DEFAULT_DEBOUNCE_K):
            state, a = mlib.bdock_decide(
                state, snap(ut=491.0 + i, target_rel_speed=float("nan")))
            actions_seen.append(a)
        self.assertTrue(state.match_retarget_done)
        self.assertEqual(actions_seen[-1], [Action(mlib.ACTION_SET_TARGET_VESSEL)])
        self.assertEqual(state.match_nan_streak, 0)  # reset after re-target
        # Further NaN frames NEVER re-target again (one-shot latch).
        for i in range(mlib.DEFAULT_DEBOUNCE_K + 1):
            state, a = mlib.bdock_decide(
                state, snap(ut=500.0 + i, target_rel_speed=float("nan")))
            self.assertNotIn(Action(mlib.ACTION_SET_TARGET_VESSEL), a)
        self.assertEqual(state.phase, mlib.BDOCK_MATCH_VELOCITY)

    def test_reacquire_then_finite_completes(self):
        state = self._at_match()
        for i in range(mlib.DEFAULT_DEBOUNCE_K):
            state, _ = mlib.bdock_decide(
                state, snap(ut=491.0 + i, target_rel_speed=float("nan")))
        self.assertTrue(state.match_retarget_done)
        # Target re-acquired -> a finite below-floor reading completes to DOCK.
        state, actions = mlib.bdock_decide(state, snap(ut=510.0, target_rel_speed=0.4))
        self.assertEqual(state.phase, mlib.BDOCK_DOCK)


class BDockDockTests(unittest.TestCase):
    def _at_dock(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_DOCK)
        # Consume the flight-9 deferred-enable poll: DOCK entry armed
        # dock_enable_pending (port target set on the entry batch); the next DOCK
        # poll emits MJ_ENABLE_DOCKING once core.target has synced. Tests below
        # start from the docking-ready state (pending cleared).
        self.assertTrue(state.dock_enable_pending)
        state, enable_actions = mlib.bdock_decide(
            state, snap(ut=502.0, target_distance=90.0))
        self.assertEqual(enable_actions,
                         [Action(mlib.ACTION_MJ_ENABLE_DOCKING, value=0.5)])
        self.assertFalse(state.dock_enable_pending)
        return state

    def test_docked_and_latch_advances_to_transfer_with_t1(self):
        state = self._at_dock()
        state, _ = mlib.bdock_decide(state, snap(
            ut=510.0, mj_docking_enabled=True, docking_state="Docking"))
        self.assertTrue(state.docking_ever_enabled)
        state, actions = mlib.bdock_decide(state, snap(
            ut=520.0, mj_docking_enabled=False, docking_state="Docked"))
        self.assertEqual(state.phase, mlib.BDOCK_TRANSFER)
        self.assertTrue(state.docked_confirmed)
        self.assertTrue(state.current_transfer_started)
        self.assertIn(Action(mlib.ACTION_START_RESOURCE_TRANSFER, value=40.0,
                             text="LiquidFuel", limit=mlib.TRANSFER_DIR_DELIVER),
                      actions)

    def test_docked_without_latch_still_advances(self):
        # Review follow-up 4: the docked short-circuit completes on `docked` ALONE
        # (not docked AND latched_off). A docked pair whose AP never latched off
        # (docking_ever_enabled still False) must NOT be misrouted into the E1a
        # "enable never took" flake -- the pair IS mated, so complete to TRANSFER.
        state = self._at_dock()
        self.assertFalse(state.docking_ever_enabled)
        state, actions = mlib.bdock_decide(state, snap(
            ut=510.0, mj_docking_enabled=False, docking_state="Docked"))
        self.assertEqual(state.phase, mlib.BDOCK_TRANSFER)
        self.assertTrue(state.docked_confirmed)
        self.assertIn(Action(mlib.ACTION_MJ_DISABLE_DOCKING), actions)

    def test_docked_on_pending_enable_poll_does_not_reenable_ap(self):
        # Review follow-up 4 (the race the fix targets): a hard dock can land on
        # the SAME poll a retarget armed dock_enable_pending. The docked
        # short-circuit sits AHEAD of the pending-enable branch, so the pending
        # enable is DISCARDED (no ACTION_MJ_ENABLE_DOCKING re-issued onto a mated
        # pair -- an unguarded runner enable could throw and flake a won mission)
        # and the phase completes to TRANSFER.
        state = self._at_dock()
        # Arm dock_enable_pending exactly as a DOCK retarget would (flight-11
        # dropped-target recovery re-arms it), then read Docked on the same poll.
        state = mlib.replace(state, dock_enable_pending=True)
        state, actions = mlib.bdock_decide(state, snap(
            ut=512.0, mj_docking_enabled=True, docking_state="Docked"))
        self.assertEqual(state.phase, mlib.BDOCK_TRANSFER)
        self.assertFalse(state.dock_enable_pending)
        self.assertTrue(state.docked_confirmed)
        # No re-enable onto the docked pair; the AP is DISABLED and T1 starts.
        kinds = [a.kind for a in actions]
        self.assertNotIn(mlib.ACTION_MJ_ENABLE_DOCKING, kinds)
        self.assertIn(mlib.ACTION_MJ_DISABLE_DOCKING, kinds)
        self.assertIn(Action(mlib.ACTION_START_RESOURCE_TRANSFER, value=40.0,
                             text="LiquidFuel", limit=mlib.TRANSFER_DIR_DELIVER),
                      actions)

    def test_monoprop_out_gives_up(self):
        state = self._at_dock()
        state, actions = mlib.bdock_decide(state, snap(
            ut=515.0, mj_docking_enabled=True, docking_state="Docking",
            monopropellant=0.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertIn(Action(mlib.ACTION_MJ_DISABLE_DOCKING), actions)

    def test_dock_budget_flakes_and_disables_ap(self):
        state = self._at_dock()
        state, actions = mlib.bdock_decide(state, snap(
            ut=500.0 + 700.0, mj_docking_enabled=True, docking_state="Docking",
            monopropellant=100.0))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertIn(Action(mlib.ACTION_MJ_DISABLE_DOCKING), actions)

    def test_dock_entry_aborts_node_exec_first(self):
        # Flight-8 prox-ops rule: MATCH-VELOCITY completion enters DOCK with the
        # node-exec abort as the FIRST action. Flight-9 stagger: no same-batch
        # enable. Flight-10: attitude hold (SAS + RCS) before the AP takes over.
        state, _ = _bdock_walk_to(mlib.BDOCK_MATCH_VELOCITY)
        state, actions = mlib.bdock_decide(state, snap(ut=495.0, target_rel_speed=0.5))
        self.assertEqual(state.phase, mlib.BDOCK_DOCK)
        self.assertEqual(actions, [
            Action(mlib.ACTION_MJ_ABORT_NODE_EXEC),
            Action(mlib.ACTION_SET_SAS),
            Action(mlib.ACTION_SET_RCS, value=1.0),
            Action(mlib.ACTION_SET_TARGET_DOCKING_PORT)])
        self.assertNotIn(Action(mlib.ACTION_MJ_ENABLE_DOCKING, value=0.5), actions)

    def test_dock_entry_then_next_step_enables(self):
        # Flight-9 stagger contract: DOCK entry sets the port target + arms the
        # deferred enable (MechJeb's core.target syncs on its NEXT Update, so a
        # same-batch enable makes the AP's first Drive tick see the OLD vessel
        # target and NRE). The enable arrives as the SOLE action on the FOLLOWING
        # step, pending cleared.
        state, _ = _bdock_walk_to(mlib.BDOCK_MATCH_VELOCITY)
        state, entry = mlib.bdock_decide(state, snap(ut=495.0, target_rel_speed=0.5))
        self.assertEqual(state.phase, mlib.BDOCK_DOCK)
        self.assertEqual(entry, [
            Action(mlib.ACTION_MJ_ABORT_NODE_EXEC),
            Action(mlib.ACTION_SET_SAS),
            Action(mlib.ACTION_SET_RCS, value=1.0),
            Action(mlib.ACTION_SET_TARGET_DOCKING_PORT)])
        self.assertTrue(state.dock_enable_pending)
        # Next poll: the enable is the SOLE action; no re-target, no double enable.
        state, nxt = mlib.bdock_decide(state, snap(ut=496.0, target_distance=90.0))
        self.assertEqual(nxt, [Action(mlib.ACTION_MJ_ENABLE_DOCKING, value=0.5)])
        self.assertFalse(state.dock_enable_pending)
        self.assertEqual(state.phase, mlib.BDOCK_DOCK)

    def test_dropped_target_first_reacquire_staggers_enable(self):
        state = self._at_dock()
        # Engage the docking AP (sets docking_ever_enabled), target still readable.
        state, _ = mlib.bdock_decide(state, snap(
            ut=505.0, mj_docking_enabled=True, docking_state="Docking",
            target_distance=50.0))
        self.assertTrue(state.docking_ever_enabled)
        # Target goes null: K consecutive NaN-distance frames -> a re-target that
        # SETS the port only (flight-9 stagger) + arms the deferred enable.
        actions_seen = []
        for i in range(mlib.DEFAULT_DEBOUNCE_K):
            state, a = mlib.bdock_decide(state, snap(
                ut=510.0 + i, mj_docking_enabled=True, docking_state="Docking",
                target_distance=float("nan"), monopropellant=100.0))
            actions_seen.append(a)
        self.assertEqual(state.dock_retarget_count, 1)
        self.assertEqual(actions_seen[-1], [Action(mlib.ACTION_SET_TARGET_DOCKING_PORT)])
        self.assertTrue(state.dock_enable_pending)
        self.assertEqual(state.dock_nan_streak, 0)  # reset after re-target
        # The deferred enable lands on the next poll as the SOLE action.
        state, enable = mlib.bdock_decide(state, snap(
            ut=515.0, mj_docking_enabled=True, docking_state="Docking",
            target_distance=float("nan"), monopropellant=100.0))
        self.assertEqual(enable, [Action(mlib.ACTION_MJ_ENABLE_DOCKING, value=0.5)])
        self.assertFalse(state.dock_enable_pending)

    def test_dropped_target_rearm_bounded_at_max(self):
        # Flight-11: the retarget is RE-ARMABLE, bounded at BDOCK_DOCK_MAX_RETARGETS
        # (one-shot was too stingy if KSP clears the port repeatedly).
        state = self._at_dock()
        state, _ = mlib.bdock_decide(state, snap(
            ut=505.0, mj_docking_enabled=True, docking_state="Docking",
            target_distance=50.0))
        retargets = 0
        ut = 510.0
        for _ in range((mlib.BDOCK_DOCK_MAX_RETARGETS + 2) * (mlib.DEFAULT_DEBOUNCE_K + 1)):
            state, a = mlib.bdock_decide(state, snap(
                ut=ut, altitude=ut, mj_docking_enabled=True, docking_state="Docking",
                target_distance=float("nan"), monopropellant=100.0))
            ut += 1.0
            if Action(mlib.ACTION_SET_TARGET_DOCKING_PORT) in a:
                retargets += 1
                # Consume the staggered deferred enable.
                state, _ = mlib.bdock_decide(state, snap(
                    ut=ut, altitude=ut, mj_docking_enabled=True, docking_state="Docking",
                    target_distance=float("nan"), monopropellant=100.0))
                ut += 1.0
            if state.done:
                break
        self.assertEqual(retargets, mlib.BDOCK_DOCK_MAX_RETARGETS)
        self.assertEqual(state.dock_retarget_count, mlib.BDOCK_DOCK_MAX_RETARGETS)

    def test_nan_distance_never_docks_fail_closed(self):
        state = self._at_dock()
        state, _ = mlib.bdock_decide(state, snap(
            ut=505.0, mj_docking_enabled=True, docking_state="Docking",
            target_distance=float("nan")))
        # NaN distance + not Docked -> never completes (docked gate reads state).
        state, _ = mlib.bdock_decide(state, snap(
            ut=506.0, mj_docking_enabled=False, docking_state="Ready",
            target_distance=float("nan"), monopropellant=100.0))
        self.assertEqual(state.phase, mlib.BDOCK_DOCK)
        self.assertFalse(state.done)

    # ---- Liveness watchdogs (flight-11 centerpiece): a dead AP never rides the
    # budget; it fails in seconds-to-minutes with a named reason. ----

    def test_enable_never_took_reissues_once_then_flakes(self):
        # E1a: the deferred enable was emitted (consumed by _at_dock) but
        # mj_docking_enabled never becomes True. After K polls re-emit ONCE; after
        # another K still not enabled -> fast FLAKE, distinctly named.
        state = self._at_dock()
        ut = 505.0
        reissued = False
        # altitude=ut keeps the frozen-telemetry signature advancing (a live craft
        # jitters its orbit every frame; a constant sig would trip vessel-lost).
        for _ in range(mlib.BDOCK_DOCK_LIVENESS_K):
            state, a = mlib.bdock_decide(state, snap(
                ut=ut, altitude=ut, mj_docking_enabled=False))
            ut += 1.0
            if Action(mlib.ACTION_MJ_ENABLE_DOCKING, value=0.5) in a:
                reissued = True
        self.assertTrue(reissued)
        self.assertTrue(state.dock_enable_reissued)
        self.assertFalse(state.done)
        for _ in range(mlib.BDOCK_DOCK_LIVENESS_K):
            state, a = mlib.bdock_decide(state, snap(
                ut=ut, altitude=ut, mj_docking_enabled=False))
            ut += 1.0
            if state.done:
                break
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        _, reason = mlib.resolve_flight_verdict(state, [])
        self.assertIn("enable did not take", reason)
        self.assertIn(Action(mlib.ACTION_MJ_DISABLE_DOCKING), a)

    def test_died_mid_approach_reenables_once_then_flakes(self):
        # E1b: the AP ran (docking_ever_enabled) then went disabled without
        # docking. After K polls re-enable ONCE (re-target first); if it dies
        # again -> fast FLAKE, distinctly named ("benched/NRE?").
        state = self._at_dock()
        state, _ = mlib.bdock_decide(state, snap(
            ut=505.0, mj_docking_enabled=True, docking_state="Docking",
            target_distance=50.0))
        self.assertTrue(state.docking_ever_enabled)
        ut = 506.0
        reenabled = False
        for _ in range(mlib.BDOCK_DOCK_LIVENESS_K):
            state, a = mlib.bdock_decide(state, snap(
                ut=ut, altitude=ut, mj_docking_enabled=False, docking_state="Docking",
                target_distance=50.0, monopropellant=100.0))
            ut += 1.0
            if Action(mlib.ACTION_SET_TARGET_DOCKING_PORT) in a:
                reenabled = True
        self.assertTrue(reenabled)
        self.assertTrue(state.dock_reenabled_after_death)
        # Consume the staggered deferred enable.
        state, _ = mlib.bdock_decide(state, snap(
            ut=ut, altitude=ut, mj_docking_enabled=False, docking_state="Docking",
            target_distance=50.0, monopropellant=100.0))
        ut += 1.0
        for _ in range(mlib.BDOCK_DOCK_LIVENESS_K):
            state, a = mlib.bdock_decide(state, snap(
                ut=ut, altitude=ut, mj_docking_enabled=False, docking_state="Docking",
                target_distance=50.0, monopropellant=100.0))
            ut += 1.0
            if state.done:
                break
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        _, reason = mlib.resolve_flight_verdict(state, [])
        self.assertIn("benched/NRE", reason)

    def test_progress_watchdog_flakes_when_all_flat(self):
        # E2: the AP is ENABLED but distance/monoprop/angvel are all flat for
        # dock_no_progress_seconds -> a dead/inert AP -> fast FLAKE, named.
        state = self._at_dock()
        p = state.params
        # First frame establishes the minima (improves from inf) + starts the clock.
        state, _ = mlib.bdock_decide(state, snap(
            ut=505.0, mj_docking_enabled=True, docking_state="Docking",
            target_distance=80.0, monopropellant=50.0, angular_velocity=0.5))
        self.assertFalse(state.done)
        # Everything flat past the window -> flake (well inside the dock budget).
        state, a = mlib.bdock_decide(state, snap(
            ut=505.0 + p.dock_no_progress_seconds + 2.0, mj_docking_enabled=True,
            docking_state="Docking", target_distance=80.0, monopropellant=50.0,
            angular_velocity=0.5))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        _, reason = mlib.resolve_flight_verdict(state, [])
        self.assertIn("no observable progress", reason)
        self.assertIn(Action(mlib.ACTION_MJ_DISABLE_DOCKING), a)

    def test_progress_watchdog_resets_on_closing_distance(self):
        # A closing distance is progress: the watchdog never fires while the AP is
        # actually approaching, even well past dock_no_progress_seconds.
        state = self._at_dock()
        p = state.params
        dist = 400.0
        ut = 505.0
        for _ in range(int(p.dock_no_progress_seconds) + 40):
            state, _ = mlib.bdock_decide(state, snap(
                ut=ut, altitude=ut, mj_docking_enabled=True, docking_state="Docking",
                target_distance=dist, monopropellant=50.0, angular_velocity=0.5))
            self.assertFalse(state.done, "closing distance must not flake")
            dist -= 2.0  # closing
            ut += 1.0
        self.assertEqual(state.phase, mlib.BDOCK_DOCK)


class BDockTransferUndockTests(unittest.TestCase):
    def _at_transfer(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_TRANSFER)
        return state

    def test_t1_completion_starts_t2(self):
        state = self._at_transfer()
        self.assertEqual(state.transfers_done, 0)
        state, actions = mlib.bdock_decide(state, snap(
            ut=525.0, transfer_complete=True, vessel_count=1))
        self.assertEqual(state.transfers_done, 1)
        self.assertEqual(state.phase, mlib.BDOCK_TRANSFER)
        self.assertEqual(actions, [Action(mlib.ACTION_START_RESOURCE_TRANSFER,
                                          value=15.0, text="MonoPropellant",
                                          limit=mlib.TRANSFER_DIR_PICKUP)])

    def test_t2_completion_undocks(self):
        state = self._at_transfer()
        state, _ = mlib.bdock_decide(state, snap(ut=525.0, transfer_complete=True,
                                                 vessel_count=1))
        state, actions = mlib.bdock_decide(state, snap(
            ut=530.0, transfer_complete=True, vessel_count=1))
        self.assertEqual(state.transfers_done, 2)
        self.assertEqual(state.phase, mlib.BDOCK_UNDOCK)
        self.assertEqual(state.undock_baseline_vessel_count, 1)
        self.assertEqual(actions, [Action(mlib.ACTION_UNDOCK)])

    def test_transfer_budget_flakes(self):
        state = self._at_transfer()
        # transfer never completes; TRANSFER budget = 2 * transfer_timeout = 240 s.
        state, _ = mlib.bdock_decide(state, snap(ut=520.0 + 250.0, transfer_complete=False))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)

    def test_transfer_stall_flakes_fast_with_named_reason(self):
        # Flight-11 liveness: transfer_amount flat for BDOCK_TRANSFER_STALL_FRAMES
        # (dry source / full dest) flakes FAST -- well inside the 240 s budget --
        # with a distinctly named reason.
        state = self._at_transfer()
        ut = 521.0
        for _ in range(mlib.BDOCK_TRANSFER_STALL_FRAMES + 2):
            state, _ = mlib.bdock_decide(state, snap(
                ut=ut, transfer_complete=False, transfer_amount=0.0))
            ut += 1.0
            if state.done:
                break
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        # Fast: flaked far below the 240 s transfer budget.
        self.assertLess(ut - 520.0, 100.0)
        _, reason = mlib.resolve_flight_verdict(state, [])
        self.assertIn("transfer stalled", reason)

    def test_undock_split_requires_vessel_count_increase_and_not_docked(self):
        state = self._at_transfer()
        state, _ = mlib.bdock_decide(state, snap(ut=525.0, transfer_complete=True,
                                                 vessel_count=1))
        state, _ = mlib.bdock_decide(state, snap(ut=530.0, transfer_complete=True,
                                                 vessel_count=1))
        self.assertEqual(state.phase, mlib.BDOCK_UNDOCK)
        # MINOR 10: vessel_count increase AND state != Docked. Count same + still
        # Docked -> no split.
        state, _ = mlib.bdock_decide(state, snap(ut=531.0, vessel_count=1,
                                                 docking_state="Docked"))
        self.assertEqual(state.phase, mlib.BDOCK_UNDOCK)
        # Count increased + not Docked -> TERMINAL.
        state, actions = mlib.bdock_decide(state, snap(ut=532.0, vessel_count=2,
                                                       docking_state="Ready"))
        self.assertEqual(state.phase, mlib.BDOCK_TERMINAL)
        self.assertTrue(state.undock_confirmed)
        self.assertEqual(actions, [Action(mlib.ACTION_CANCEL_WARP)])

    def test_undock_ready_alone_is_soft_evidence(self):
        # Ready with NO count increase is soft evidence only (the port lingers
        # Undocking inside ReengageDistance) -> no split.
        state = self._at_transfer()
        state, _ = mlib.bdock_decide(state, snap(ut=525.0, transfer_complete=True,
                                                 vessel_count=1))
        state, _ = mlib.bdock_decide(state, snap(ut=530.0, transfer_complete=True,
                                                 vessel_count=1))
        state, _ = mlib.bdock_decide(state, snap(ut=531.0, vessel_count=1,
                                                 docking_state="Ready"))
        self.assertEqual(state.phase, mlib.BDOCK_UNDOCK)


class BDockLossTests(unittest.TestCase):
    def test_vessel_lost_terminal_outside_int_launch(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_STATION_ASCENT)
        state, _ = mlib.bdock_decide(state, snap(ut=50.0, vessel_lost=True))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", state.loss_reason)

    def test_vessel_lost_during_int_launch_is_transient(self):
        # Reach INT-LAUNCH, then a vessel_lost mid-reload must NOT terminate.
        state, _ = _bdock_walk_to(mlib.BDOCK_STATION_COMMIT)
        state, _ = mlib.bdock_decide(state, snap(ut=161.0, seam_commit_result="OK"))
        self.assertEqual(state.phase, mlib.BDOCK_INT_LAUNCH)
        state, _ = mlib.bdock_decide(state, snap(ut=165.0, vessel_lost=True))
        self.assertFalse(state.done)
        self.assertEqual(state.phase, mlib.BDOCK_INT_LAUNCH)

    def test_frozen_telemetry_terminal_in_flight_phase(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_STATION_ASCENT)
        # Feed frozen_sample_limit (10) bit-identical airborne frames at 1x.
        for i in range(12):
            state, _ = mlib.bdock_decide(state, snap(
                ut=200.0 + i, altitude=5000.0, apoapsis=50000.0, periapsis=1000.0,
                vertical_speed=100.0))
            if state.done:
                break
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)


class BDockAssertionTests(unittest.TestCase):
    def test_all_met_on_full_terminal(self):
        state, _ = _bdock_walk_to(mlib.BDOCK_TERMINAL)
        outs = mlib.evaluate_bdock_assertions(
            [], BDOCK_PARAMS,
            phases_reached=state.phases_reached, state=state)
        self.assertEqual([o.name for o in outs],
                         ["reachedStationOrbit", "stationSeparated",
                          "reachedInterceptorOrbit", "interceptorSeparated",
                          "docked", "transfersComplete", "undocked"])
        self.assertTrue(mlib.all_assertions_met(outs))

    def test_docked_unmet_without_evidence(self):
        # Phase reached but docked_confirmed False (fail-closed) -> unmet.
        class _S:
            docked_confirmed = False
            transfers_done = 0
            undock_confirmed = False
        outs = mlib.evaluate_bdock_assertions(
            [], BDOCK_PARAMS,
            phases_reached=(mlib.BDOCK_DOCK,), state=_S())
        docked = next(o for o in outs if o.name == "docked")
        self.assertFalse(docked.met)

    def test_transfers_unmet_with_one(self):
        class _S:
            docked_confirmed = True
            transfers_done = 1
            undock_confirmed = False
        outs = mlib.evaluate_bdock_assertions(
            [], BDOCK_PARAMS,
            phases_reached=(mlib.BDOCK_DOCK, mlib.BDOCK_TRANSFER), state=_S())
        transfers = next(o for o in outs if o.name == "transfersComplete")
        self.assertFalse(transfers.met)
        self.assertEqual(transfers.value, 1)


class BDockParamTests(unittest.TestCase):
    def test_params_from_dict_reads_all_keys(self):
        p = mlib.bdock_params_from_dict({
            "stationApoapsisMeters": 111000, "interceptorApoapsisMeters": 91000,
            "approachDistanceMeters": 120, "transferAmountLf": 42,
            "transferAmountMp": 16, "maxPhasingOrbits": 6,
        })
        self.assertEqual(p.station_apoapsis, 111000.0)
        self.assertEqual(p.interceptor_apoapsis, 91000.0)
        self.assertEqual(p.approach_distance, 120.0)
        self.assertEqual(p.transfer_amount_lf, 42.0)
        self.assertEqual(p.transfer_amount_mp, 16.0)
        self.assertEqual(p.max_phasing_orbits, 6.0)

    def test_forge_params_from_dict_defaults_crew_none(self):
        p = mlib.forge_params_from_dict({"craftName": "Kerbal X"})
        self.assertEqual(p.craft_name, "Kerbal X")
        self.assertIsNone(p.crew_names)
        self.assertEqual(p.launch_site, "LaunchPad")

    def test_forge_params_from_dict_parses_named_crew(self):
        # The EVA-3 3-crew pad fixture passes crewNames (a list of kerbal names);
        # forge_params_from_dict must carry it as a tuple onto the launch Action.
        p = mlib.forge_params_from_dict({
            "craftName": "Kerbal X",
            "crewNames": ["Valentina Kerman", "Bob Kerman", "Bill Kerman"],
        })
        self.assertEqual(p.crew_names,
                         ("Valentina Kerman", "Bob Kerman", "Bill Kerman"))

    def test_forge_decide_threads_launch_site_and_named_crew(self):
        # The launch Action must carry launch_site + crew (by NAME) so the runner
        # threads both into sc.launch_vessel; an empty/None crewNames stays None
        # (runner sends crew=[] = default manifest).
        params = mlib.ForgeParams(
            craft_name="Kerbal X", launch_site="LaunchPad",
            crew_names=("Valentina Kerman", "Bob Kerman", "Bill Kerman"),
            launch_timeout=120.0, settle_debounce=3)
        state = mlib.forge_initial_state(params)
        _, actions = mlib.forge_decide(state, snap(ut=0.0))
        self.assertEqual(actions, [Action(
            mlib.ACTION_LAUNCH_VESSEL, text="Kerbal X",
            launch_site="LaunchPad",
            crew=("Valentina Kerman", "Bob Kerman", "Bill Kerman"))])


class BDockPortResolutionTests(unittest.TestCase):
    """Pure helpers behind the flight-13 LIVE port/state resolution (the runner
    reads the live kRPC states and applies these)."""

    def test_pick_ready_port_prefers_first_ready(self):
        self.assertEqual(mlib.pick_ready_port_index(["docked", "ready", "ready"]), 1)

    def test_pick_ready_port_case_insensitive(self):
        self.assertEqual(mlib.pick_ready_port_index(["Docked", "READY"]), 1)

    def test_pick_ready_port_falls_back_to_first_when_none_ready(self):
        self.assertEqual(mlib.pick_ready_port_index(["docked", "docking"]), 0)

    def test_pick_ready_port_none_when_empty(self):
        self.assertIsNone(mlib.pick_ready_port_index([]))
        self.assertIsNone(mlib.pick_ready_port_index(None))

    def test_normalize_docking_state_pascalcases(self):
        self.assertEqual(mlib.normalize_docking_state("docked"), "Docked")
        self.assertEqual(mlib.normalize_docking_state("pre_attached"), "PreAttached")
        self.assertEqual(mlib.normalize_docking_state("ready"), mlib.DOCKING_STATE_READY)

    def test_normalize_docking_state_empty_is_failclosed(self):
        self.assertEqual(mlib.normalize_docking_state(""), "")
        self.assertEqual(mlib.normalize_docking_state(None), "")


if __name__ == "__main__":
    unittest.main()
