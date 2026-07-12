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

    def test_prelaunch_engages_mechjeb(self):
        state = mlib.b2_initial_state(B2_PARAMS)
        new, actions = mlib.b2_decide(state, snap(ut=0.0))
        self.assertEqual(new.phase, mlib.B2_MJ_ASCENT)
        self.assertEqual(actions, [
            Action(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, 80000.0),
            Action(mlib.ACTION_MJ_ENABLE_AUTOSTAGE),
            Action(mlib.ACTION_MJ_ENGAGE_ASCENT),
        ])

    def test_full_happy_path(self):
        # Regression: MJ-ASCENT holds until apoapsis at target, CIRCULARIZE holds
        # until periapsis at target, then ORBIT.
        state = mlib.b2_initial_state(B2_PARAMS)
        frames = [
            snap(ut=0.0),                                       # PRELAUNCH->MJ-ASCENT
            snap(ut=60.0, apoapsis=40000.0),                    # MJ-ASCENT (climbing)
            snap(ut=180.0, apoapsis=78000.0),                   # MJ-ASCENT->CIRCULARIZE (>=75000)
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
    masks a real miss."""

    def test_single_outlier_tolerated(self):
        # One out-of-window frame between good frames -> still MET (a later run of
        # K=3 in-window frames settles it).
        frames = [snap(apoapsis=14000.0), snap(apoapsis=14000.0),
                  snap(apoapsis=999999.0),  # warp-edge outlier
                  snap(apoapsis=14000.0), snap(apoapsis=14000.0), snap(apoapsis=14000.0)]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertTrue(outs[0].met)

    def test_single_nan_frame_tolerated(self):
        frames = [snap(apoapsis=14000.0), snap(apoapsis=14000.0),
                  snap(apoapsis=float("nan")),
                  snap(apoapsis=14000.0), snap(apoapsis=14000.0), snap(apoapsis=14000.0)]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertTrue(outs[0].met)

    def test_persistent_out_of_tolerance_fails(self):
        # A sustained out-of-window run never reaches K consecutive in-window.
        frames = [snap(apoapsis=40000.0) for _ in range(6)]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertFalse(outs[0].met)

    def test_scattered_good_frames_never_settle(self):
        # Alternating in/out never yields K=3 consecutive -> UNMET (a real miss
        # peeking through warp noise must not be masked).
        frames = [snap(apoapsis=14000.0), snap(apoapsis=40000.0),
                  snap(apoapsis=14000.0), snap(apoapsis=40000.0),
                  snap(apoapsis=14000.0), snap(apoapsis=40000.0)]
        outs = mlib.evaluate_b1_assertions(frames, B1_PARAMS)
        self.assertFalse(outs[0].met)

    def test_has_k_consecutive_primitive(self):
        self.assertTrue(mlib._has_k_consecutive_true([False, True, True, True], 3))
        self.assertFalse(mlib._has_k_consecutive_true([True, True, False, True], 3))
        # k<=0 clamps to 1 (an empty sequence still fails).
        self.assertFalse(mlib._has_k_consecutive_true([], 0))
        self.assertTrue(mlib._has_k_consecutive_true([True], 0))

    def test_custom_k_of_one(self):
        # With k=1 a single in-window frame suffices.
        outs = mlib.evaluate_b1_assertions([snap(apoapsis=14000.0)], B1_PARAMS, k=1)
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


if __name__ == "__main__":
    unittest.main()
